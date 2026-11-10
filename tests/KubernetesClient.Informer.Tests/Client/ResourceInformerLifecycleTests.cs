// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using KubernetesClient.Informer.Common;

namespace KubernetesClient.Informer.Client.Tests;

public class ResourceInformerLifecycleTests
{
    [Fact]
    public async Task RunAsyncThrowsWhenCancellationIsAlreadyRequested()
    {
        using var informer = CreateInformer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsyncStopsWaitingForStartWatchingWhenCancellationIsRequested()
    {
        using var informer = CreateInformer();
        using var cancellation = new CancellationTokenSource();
        var runTask = informer.RunAsync(cancellation.Token);

        Assert.False(runTask.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsyncCancellationBeforeInitialListTerminatesReadiness()
    {
        using var informer = CreateInformer();
        using var cancellation = new CancellationTokenSource();
        var runTask = informer.RunAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.ReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistrationAfterRunStopsBeforeInitialSyncCannotBecomeReady()
    {
        using var informer = CreateInformer();
        using var cancellation = new CancellationTokenSource();
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => { }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registration.ReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentRunAsyncCallsAreRejected()
    {
        using var informer = CreateInformer();
        using var cancellation = new CancellationTokenSource();
        var runTask = informer.RunAsync(cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => informer.RunAsync(CancellationToken.None));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RegisterRejectsNullCallback()
    {
        using var informer = CreateInformer();

        Assert.Throws<ArgumentNullException>(
            () => informer.Register((ResourceInformerCallback<V1Pod>)null!));
        Assert.Throws<ArgumentNullException>(
            () => informer.Register((ResourceInformerCallback<IKubernetesObject<V1ObjectMeta>>)null!));
    }

    [Fact]
    public async Task RunInfinitePassesCancellationTokenToRunAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var informer = new CancellationAwareInformer();
        var runTask = informer.RunInfinite(cancellation.Token);

        await informer.TokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(cancellation.Token, informer.ObservedToken);

        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunInfiniteStopsWhenCancellationIsRequested()
    {
        using var cancellation = new CancellationTokenSource();
        using var informer = new CancellationAwareInformer();
        var runTask = informer.RunInfinite(cancellation.Token);

        await informer.TokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, informer.RunCount);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }

    [Fact]
    public void DisposeCanBeCalledMoreThanOnce()
    {
        using var informer = CreateInformer();

        informer.Dispose();
        informer.Dispose();
    }

    [Fact]
    public void RegistrationCanBeDisposedMoreThanOnce()
    {
        using var informer = CreateInformer();
        var registration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => { }));

        registration.Dispose();
        registration.Dispose();
    }

    [Fact]
    public void RegisterAfterInformerDisposalThrows()
    {
        using var informer = CreateInformer();
        informer.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => { })));
    }

    [Fact]
    public async Task DisposeDoesNotInvalidateReadinessSignal()
    {
        using var informer = CreateInformer();
        var readiness = informer.ReadyAsync(CancellationToken.None);

        Assert.False(readiness.IsCompleted);
        informer.Dispose();
        Assert.False(readiness.IsCompleted);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.ReadyAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeCancelsAnActiveRun()
    {
        using var informer = new DisposableAwareInformer();
        await informer.StartAsync(CancellationToken.None);
        await informer.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        informer.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.RunTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunInfiniteDoesNotRetryWhenDependencyUsesAnotherCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var dependencyCancellation = new CancellationTokenSource();
        var logger = new RecordingLogger();
        using var informer = new UnexpectedCancellationInformer(logger, dependencyCancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.RunInfinite(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.False(logger.RetryWasLogged);
        Assert.Equal(ResourceInformerStatus.Faulted, informer.Status);
    }

    [Fact]
    public async Task RunInfiniteRetriesUnexpectedRunFailureAfterBackoff()
    {
        using var cancellation = new CancellationTokenSource();
        var timeProvider = new TestTimeProvider();
        var logger = new RecordingLogger();
        using var informer = new RetryOnceInformer(timeProvider, logger);

        var runTask = informer.RunInfinite(cancellation.Token);
        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();

        var backoff = timeProvider.GetTimerDueTime(0);
        Assert.InRange(backoff, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));
        timeProvider.FireTimer(0);
        await informer.SecondRunStarted.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, informer.RunCount);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
        var retryLog = Assert.Single(logger.Entries, entry => entry.EventId.Id == 110);
        Assert.Equal(LogLevel.Error, retryLog.LogLevel);
        Assert.IsType<InvalidOperationException>(retryLog.Exception);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 109 && entry.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task RunInfiniteWithPreCanceledTokenDoesNotStartRunAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var informer = new CancellationAwareInformer();

        await informer.RunInfinite(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, informer.RunCount);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.ReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentRunInfiniteCallsAreRejected()
    {
        using var cancellation = new CancellationTokenSource();
        using var informer = new CancellationAwareInformer();
        var runTask = informer.RunInfinite(cancellation.Token);
        await informer.TokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => informer.RunInfinite(CancellationToken.None));

        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, informer.RunCount);
    }

    [Fact]
    public async Task RunInfiniteIsRejectedWhileRunAsyncIsActive()
    {
        using var cancellation = new CancellationTokenSource();
        using var informer = CreateInformer();
        var runTask = informer.RunAsync(cancellation.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => informer.RunInfinite(CancellationToken.None));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private static ResourceInformer<V1Pod> CreateInformer()
    {
        return new ResourceInformer<V1Pod>(
            new Mock<IKubernetes>().Object,
            new Mock<IHostApplicationLifetime>().Object,
            NullLogger<ResourceInformer<V1Pod>>.Instance);
    }

    private sealed class CancellationAwareInformer : ResourceInformer<V1Pod>
    {
        public CancellationAwareInformer()
            : base(
                new Mock<IKubernetes>().Object,
                new Mock<IHostApplicationLifetime>().Object,
                NullLogger<ResourceInformer<V1Pod>>.Instance)
        {
        }

        public TaskCompletionSource TokenObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        public int RunCount { get; private set; }

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            RunCount++;
            ObservedToken = cancellationToken;
            TokenObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class RetryOnceInformer : ResourceInformer<V1Pod>
    {
        private int _runCount;

        public RetryOnceInformer(TimeProvider timeProvider, ILogger<ResourceInformer<V1Pod>> logger)
            : base(
                new Mock<IKubernetes>().Object,
                new Mock<IHostApplicationLifetime>().Object,
                logger,
                timeProvider: timeProvider)
        {
        }

        public int RunCount => Volatile.Read(ref _runCount);

        public TaskCompletionSource SecondRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task RunAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runCount) == 1)
            {
                throw new InvalidOperationException("Initial connection failed.");
            }

            SecondRunStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class DisposableAwareInformer : ResourceInformer<V1Pod>
    {
        private Task? _runTask;

        public DisposableAwareInformer()
            : base(
                new Mock<IKubernetes>().Object,
                new Mock<IHostApplicationLifetime>().Object,
                NullLogger<ResourceInformer<V1Pod>>.Instance)
        {
        }

        public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunTask => _runTask ?? throw new InvalidOperationException("The informer has not started.");

        public override Task RunAsync(CancellationToken cancellationToken)
        {
            _runTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            RunStarted.TrySetResult();
            return _runTask;
        }
    }

    private sealed class UnexpectedCancellationInformer : ResourceInformer<V1Pod>
    {
        private readonly CancellationToken _dependencyCancellationToken;

        public UnexpectedCancellationInformer(
            ILogger<ResourceInformer<V1Pod>> logger,
            CancellationToken dependencyCancellationToken)
            : base(
                new Mock<IKubernetes>().Object,
                new Mock<IHostApplicationLifetime>().Object,
                logger)
        {
            _dependencyCancellationToken = dependencyCancellationToken;
        }

        public override Task RunAsync(CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(_dependencyCancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger<ResourceInformer<V1Pod>>
    {
        private readonly object _sync = new();
        private readonly List<RecordedLog> _entries = [];

        public bool RetryWasLogged { get; private set; }

        public IReadOnlyList<RecordedLog> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_sync)
            {
                _entries.Add(new RecordedLog(logLevel, eventId, exception, message));
                if (message.StartsWith("Retrying ", StringComparison.Ordinal))
                {
                    RetryWasLogged = true;
                }
            }
        }
    }

    private sealed record RecordedLog(LogLevel LogLevel, EventId EventId, Exception? Exception, string Message);
}
