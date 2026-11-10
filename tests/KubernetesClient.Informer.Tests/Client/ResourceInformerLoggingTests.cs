// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using KubernetesClient.Informer.Common;
using KubernetesClient.Informer.Tests.TestCluster;
using KubernetesClient.Informer.Tests.TestCluster.Models;

namespace KubernetesClient.Informer.Client.Tests;

public class ResourceInformerLoggingTests
{
    [Theory]
    [InlineData((int)HttpStatusCode.ServiceUnavailable, "InternalError", LogLevel.Warning)]
    [InlineData((int)HttpStatusCode.TooManyRequests, "TooManyRequests", LogLevel.Warning)]
    [InlineData((int)HttpStatusCode.Forbidden, "Forbidden", LogLevel.Error)]
    [InlineData((int)HttpStatusCode.Gone, "Expired", LogLevel.Information)]
    public async Task ListFailuresAreLoggedAtSeverityMatchingTheirImpact(
        int statusCode,
        string reason,
        LogLevel expectedLogLevel)
    {
        var error = new KubernetesException(new V1Status
        {
            Code = statusCode,
            Reason = reason,
            Message = "The list request failed.",
        });

        var entry = await CaptureFirstListFailureAsync(error);

        Assert.Equal(expectedLogLevel, entry.LogLevel);
        Assert.Equal(104, entry.EventId.Id);
        Assert.Same(error, entry.Exception);
        Assert.Contains("retrying", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RetryDelay", entry.State.Keys);
    }

    [Fact]
    public async Task WatchStreamErrorIsLoggedOnceWithRetrySeverityAndException()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var logger = new RecordingLogger();
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "ERROR",
            Object = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Status",
                Metadata = new V1ObjectMeta(),
                AdditionalData = new Dictionary<string, object>
                {
                    ["status"] = "Failure",
                    ["message"] = "Temporary API server failure.",
                    ["reason"] = "InternalError",
                    ["code"] = 500,
                },
            },
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            logger,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.WaitForWatchRequestAsync(1).WaitAsync(cancellation.Token).DefaultTimeout();
        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();

        var errorEntry = Assert.Single(logger.Entries, entry => entry.EventId.Id == 104);
        Assert.Equal(LogLevel.Warning, errorEntry.LogLevel);
        Assert.NotNull(errorEntry.Exception);
        Assert.Contains("RetryDelay", errorEntry.State.Keys);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    private static async Task<RecordedLog> CaptureFirstListFailureAsync(Exception error)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var logger = new RecordingLogger();
        var customObjects = new Mock<ICustomObjectsOperations>();
        customObjects
            .Setup(client => client.ListClusterCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<bool?>(),
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(error));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            logger,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        Assert.Equal(ResourceInformerStatus.Retrying, informer.Status);
        var entry = Assert.Single(logger.Entries, candidate => candidate.EventId.Id == 104);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        return entry;
    }

    private sealed class RecordingLogger : ILogger<ResourceInformer<V1Pod>>
    {
        private readonly ConcurrentQueue<RecordedLog> _entries = new();

        public IReadOnlyCollection<RecordedLog> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();

            _entries.Enqueue(new RecordedLog(
                logLevel,
                eventId,
                exception,
                formatter(state, exception),
                properties));
        }
    }

    private sealed record RecordedLog(
        LogLevel LogLevel,
        EventId EventId,
        Exception? Exception,
        string Message,
        IReadOnlyDictionary<string, object?> State);
}
