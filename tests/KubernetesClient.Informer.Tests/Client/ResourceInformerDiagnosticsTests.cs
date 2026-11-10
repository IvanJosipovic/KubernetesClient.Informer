// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using KubernetesClient.Informer.Common;
using KubernetesClient.Informer.Tests.TestCluster;
using KubernetesClient.Informer.Tests.TestCluster.Models;

namespace KubernetesClient.Informer.Client.Tests;

public class ResourceInformerDiagnosticsTests
{
    [Fact]
    public async Task ActivitiesExposeResourceRequestAndWatchEventTags()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var informerNamespace = $"diag-{Guid.NewGuid():N}";
        var releaseWatchResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activities = new ConcurrentBag<Activity>();
        using var parentActivity = new Activity("diagnostics-test").Start();
        using var listener = CreateListener(parentActivity.TraceId, activities);

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources([new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = "diagnostic-pod",
                    NamespaceProperty = informerNamespace,
                    ResourceVersion = "1",
                },
            }])
            .Build();
        clusterHost.Cluster.WatchResponseGate = releaseWatchResponse.Task;
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "MODIFIED",
            Object = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta
                {
                    Name = "diagnostic-pod",
                    NamespaceProperty = informerNamespace,
                    ResourceVersion = "2",
                },
            },
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var watchEventReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            if (eventType == WatchEventType.Modified && pod.Name() == "diagnostic-pod")
            {
                watchEventReceived.TrySetResult();
            }
        }));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WaitForWatchRequestAsync(1).WaitAsync(cancellation.Token).DefaultTimeout();
        releaseWatchResponse.TrySetResult();
        await watchEventReceived.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        var recordedActivities = activities.ToArray();
        var runActivity = Assert.Single(recordedActivities, activity => activity.DisplayName == nameof(ResourceInformer<V1Pod>.RunAsync));
        var listActivity = Assert.Single(recordedActivities, activity => activity.DisplayName == "ListAsync");
        var eventActivity = Assert.Single(recordedActivities, activity => activity.DisplayName == "OnEvent");
        var watchActivity = Assert.Single(recordedActivities, activity => activity.DisplayName == "WatchAsync");

        Assert.Equal(ActivityKind.Consumer, runActivity.Kind);
        Assert.Equal("V1Pod", runActivity.GetTagItem("k8s.resource.type"));
        Assert.Null(runActivity.GetTagItem("k8s.namespace.name"));

        Assert.Equal(ActivityStatusCode.Ok, listActivity.Status);
        Assert.Null(listActivity.GetTagItem("k8s.namespace.name"));
        Assert.Equal(1, listActivity.GetTagItem("k8s.informer.resource_count"));

        Assert.Equal("0", watchActivity.GetTagItem("k8s.informer.resource_version"));
        Assert.Equal("Modified", eventActivity.GetTagItem("k8s.informer.event_type"));
        Assert.Equal("diagnostic-pod", eventActivity.GetTagItem("k8s.resource.name"));
        Assert.Equal(informerNamespace, eventActivity.GetTagItem("k8s.resource.namespace"));
        Assert.Equal("2", eventActivity.GetTagItem("k8s.resource.version"));
    }

    [Fact]
    public async Task RetriedWatchErrorIsRecordedOnInformerActivity()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var activities = new ConcurrentBag<Activity>();
        using var parentActivity = new Activity("diagnostics-test").Start();
        using var listener = CreateListener(parentActivity.TraceId, activities);

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
                    ["message"] = "diagnostic watch failure",
                    ["reason"] = "InternalError",
                    ["code"] = 500,
                },
            },
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.WaitForWatchRequestAsync(1).WaitAsync(cancellation.Token).DefaultTimeout();
        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await clusterHost.Cluster.WaitForWatchRequestAsync(2).WaitAsync(cancellation.Token).DefaultTimeout();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        var runActivity = Assert.Single(activities, activity => activity.DisplayName == nameof(ResourceInformer<V1Pod>.RunAsync));
        Assert.Equal(ActivityStatusCode.Error, runActivity.Status);
        Assert.Contains(runActivity.Events, activityEvent => activityEvent.Name == "exception");
    }

    private static ActivityListener CreateListener(ActivityTraceId traceId, ConcurrentBag<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == InformerDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId)
                {
                    activities.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
