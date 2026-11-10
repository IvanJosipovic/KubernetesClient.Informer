// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using KubernetesClient.Informer.Common;

namespace KubernetesClient.Informer.Rate.Tests;

public class LimiterTests
{
    private readonly DateTimeOffset _startTime = new DateTimeOffset(2020, 10, 14, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void FirstTokenIsAvailable()
    {
        var timeProvider = new TestTimeProvider(_startTime);
        var limiter = new Limiter(new Limit(10), 1, timeProvider);

        var allowed = limiter.Allow();

        Assert.True(allowed);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1)]
    [InlineData(300)]
    public void AsManyAsBurstTokensAreAvailableRightAway(int burst)
    {
        var timeProvider = new TestTimeProvider(_startTime);
        var limiter = new Limiter(new Limit(10), burst, timeProvider);

        var allowed = new List<bool>();
        foreach (var index in Enumerable.Range(1, burst))
        {
            allowed.Add(limiter.Allow());
        }
        var notAllowed = limiter.Allow();

        Assert.All(allowed, item => Assert.True(item));
        Assert.False(notAllowed);
    }

    [Fact]
    public void TokensBecomeAvailableAtLimitPerSecondRate()
    {
        var timeProvider = new TestTimeProvider(_startTime);
        var limiter = new Limiter(new Limit(10), 50, timeProvider);

        var initiallyAllowed = limiter.AllowN(timeProvider.GetUtcNow(), 50);
        var thenNotAllowed1 = limiter.Allow();

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        var oneTokenAvailable = limiter.Allow();
        var thenNotAllowed2 = limiter.Allow();

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        var twoTokensAvailable1 = limiter.Allow();
        var twoTokensAvailable2 = limiter.Allow();
        var thenNotAllowed3 = limiter.Allow();

        Assert.True(initiallyAllowed);
        Assert.False(thenNotAllowed1);
        Assert.True(oneTokenAvailable);
        Assert.False(thenNotAllowed2);
        Assert.True(twoTokensAvailable1);
        Assert.True(twoTokensAvailable2);
        Assert.False(thenNotAllowed3);
    }

    [Fact]
    public void ReserveTellsYouHowLongToWait()
    {
        var timeProvider = new TestTimeProvider(_startTime);
        var limiter = new Limiter(new Limit(10), 50, timeProvider);

        var initiallyAllowed = limiter.AllowN(timeProvider.GetUtcNow(), 50);
        var thenNotAllowed1 = limiter.Allow();

        var reserveOne = limiter.Reserve();
        var delayOne = reserveOne.Delay();

        var reserveTwoMore = limiter.Reserve(timeProvider.GetUtcNow(), 2);
        var delayTwoMore = reserveTwoMore.Delay();

        timeProvider.Advance(TimeSpan.FromMilliseconds(450));

        var reserveAlreadyAvailable = limiter.Reserve();
        var delayAlreadyAvailable = reserveAlreadyAvailable.Delay();

        var reserveHalfAvailable = limiter.Reserve();
        var delayHalfAvailable = reserveHalfAvailable.Delay();

        Assert.True(initiallyAllowed);
        Assert.False(thenNotAllowed1);
        Assert.True(reserveOne.Ok);
        Assert.Equal(TimeSpan.FromMilliseconds(100), delayOne);
        Assert.True(reserveTwoMore.Ok);
        Assert.Equal(TimeSpan.FromMilliseconds(300), delayTwoMore);
        Assert.True(reserveAlreadyAvailable.Ok);
        Assert.Equal(TimeSpan.Zero, delayAlreadyAvailable);
        Assert.True(reserveHalfAvailable.Ok);
        Assert.Equal(TimeSpan.FromMilliseconds(50), delayHalfAvailable);
    }

    [Fact]
    public async Task WaitAsyncUsesConfiguredVirtualTimeProvider()
    {
        var timeProvider = new TestTimeProvider(_startTime);
        var limiter = new Limiter(new Limit(1), 1, timeProvider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await limiter.WaitAsync(cancellation.Token);
        var waiting = limiter.WaitAsync(cancellation.Token);
        Assert.False(waiting.IsCompleted);

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(0, TimeSpan.FromSeconds(1));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        timeProvider.FireTimer(0);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitAsyncCausesPauseLikeReserve()
    {
        var timeProvider = new TestTimeProvider(_startTime);
        var limiter = new Limiter(new Limit(10), 5, timeProvider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await ConsumeBurstAndWaitForRefillAsync(limiter, timeProvider, cancellation.Token);

        var delayOne = limiter.WaitAsync(cancellation.Token);
        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(1, TimeSpan.FromMilliseconds(100));
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        timeProvider.FireTimer(1);
        await delayOne;

        var delayTwoMore = limiter.WaitAsync(2, cancellation.Token);
        await timeProvider.WaitForTimerCountAsync(3, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(2, TimeSpan.FromMilliseconds(200));
        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        timeProvider.FireTimer(2);
        await delayTwoMore;

        timeProvider.Advance(TimeSpan.FromMilliseconds(150));
        var delayAlreadyAvailable = limiter.WaitAsync(cancellation.Token);
        Assert.True(delayAlreadyAvailable.IsCompletedSuccessfully);
        await delayAlreadyAvailable;

        var delayHalfAvailable = limiter.WaitAsync(cancellation.Token);
        await timeProvider.WaitForTimerCountAsync(4, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(3, TimeSpan.FromMilliseconds(50));
        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        timeProvider.FireTimer(3);
        await delayHalfAvailable;
    }

    [Fact]
    public async Task ManyWaitsStackUp()
    {
        var timeProvider = new TestTimeProvider(_startTime);
        var limiter = new Limiter(new Limit(10), 5, timeProvider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await ConsumeBurstAndWaitForRefillAsync(limiter, timeProvider, cancellation.Token);

        var waits = new[]
        {
            limiter.WaitAsync(cancellation.Token),
            limiter.WaitAsync(cancellation.Token),
            limiter.WaitAsync(cancellation.Token),
        };
        await timeProvider.WaitForTimerCountAsync(4, cancellation.Token).DefaultTimeout();

        timeProvider.VerifyTimer(1, TimeSpan.FromMilliseconds(100));
        timeProvider.VerifyTimer(2, TimeSpan.FromMilliseconds(100));
        timeProvider.VerifyTimer(3, TimeSpan.FromMilliseconds(100));

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        timeProvider.FireTimer(1);
        Assert.Same(waits[0], await Task.WhenAny(waits));
        await waits[0];
        Assert.False(waits[1].IsCompleted);
        Assert.False(waits[2].IsCompleted);

        timeProvider.FireTimer(2);
        await timeProvider.WaitForTimerCountAsync(5, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(4, TimeSpan.FromMilliseconds(100));
        timeProvider.FireTimer(3);
        await timeProvider.WaitForTimerCountAsync(6, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(5, TimeSpan.FromMilliseconds(100));

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        timeProvider.FireTimer(4);
        await waits[1];
        Assert.False(waits[2].IsCompleted);

        timeProvider.FireTimer(5);
        await timeProvider.WaitForTimerCountAsync(7, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(6, TimeSpan.FromMilliseconds(100));
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        timeProvider.FireTimer(6);
        await waits[2];
    }

    private static async Task ConsumeBurstAndWaitForRefillAsync(
        Limiter limiter,
        TestTimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 5; i++)
        {
            await limiter.WaitAsync(cancellationToken);
        }

        var waitForRefill = limiter.WaitAsync(cancellationToken);
        await timeProvider.WaitForTimerCountAsync(1, cancellationToken).DefaultTimeout();
        timeProvider.VerifyTimer(0, TimeSpan.FromMilliseconds(100));
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        timeProvider.FireTimer(0);
        await waitForRefill;
    }
}
