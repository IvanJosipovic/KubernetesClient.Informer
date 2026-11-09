// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

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
            () => informer.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RunAsyncStopsWaitingForStartWatchingWhenCancellationIsRequested()
    {
        using var informer = CreateInformer();
        using var cancellation = new CancellationTokenSource();
        var runTask = informer.RunAsync(cancellation.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(runTask.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runTask.WaitAsync(TimeSpan.FromSeconds(5)));
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

        await informer.TokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(cancellation.Token, informer.ObservedToken);

        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunInfiniteStopsWhenCancellationIsRequested()
    {
        using var cancellation = new CancellationTokenSource();
        using var informer = new CancellationAwareInformer();
        var runTask = informer.RunInfinite(cancellation.Token);

        await informer.TokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

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
    public async Task DisposeDoesNotInvalidateReadinessSignal()
    {
        using var informer = CreateInformer();
        informer.Dispose();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.ReadyAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisposeCancelsAnActiveRun()
    {
        using var informer = new DisposableAwareInformer();
        await informer.StartAsync(CancellationToken.None);
        await informer.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        informer.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RunInfiniteDoesNotRetryWhenDependencyUsesAnotherCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var dependencyCancellation = new CancellationTokenSource();
        var logger = new RecordingLogger();
        using var informer = new UnexpectedCancellationInformer(dependencyCancellation.Token, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => informer.RunInfinite(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.False(logger.RetryWasLogged);
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
            CancellationToken dependencyCancellationToken,
            ILogger<ResourceInformer<V1Pod>> logger)
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
        public bool RetryWasLogged { get; private set; }

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
            if (formatter(state, exception).StartsWith("Retrying ", StringComparison.Ordinal))
            {
                RetryWasLogged = true;
            }
        }
    }
}
