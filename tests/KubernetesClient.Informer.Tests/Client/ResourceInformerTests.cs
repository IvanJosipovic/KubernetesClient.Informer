// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Xunit;
using KubernetesClient.Informer.Common;
using KubernetesClient.Informer.Tests.TestCluster;
using KubernetesClient.Informer.Tests.TestCluster.Models;
using KubernetesClient.Informer.Tests.Utils;
using Moq;

namespace KubernetesClient.Informer.Client.Tests;

public class ResourceInformerTests
{
    [Fact]
    public void ConstructorUsesSpecifiedNamesOrResourceNamesWhenNamesAreNull()
    {
        var specifiedNames = new GroupApiVersionKind("custom.example.com", "v1", "CustomResource", "customresources");

        using var informerWithSpecifiedNames = new ResourceInformer<V1Pod>(
            new Mock<IKubernetes>().Object,
            new Mock<IHostApplicationLifetime>().Object,
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            groupApiVersionKind: specifiedNames);
        using var informerWithDefaultNames = new ResourceInformer<V1Pod>(
            new Mock<IKubernetes>().Object,
            new Mock<IHostApplicationLifetime>().Object,
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        var namesField = typeof(ResourceInformer<V1Pod>).GetField("_groupApiVersionKind", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(namesField);
        Assert.Equal(specifiedNames, namesField.GetValue(informerWithSpecifiedNames));
        Assert.Equal(GroupApiVersionKind.From<V1Pod>(), namesField.GetValue(informerWithDefaultNames));
    }

    private static (TResources Resources, TShouldBe ShouldBe) LoadTestResource<TResources, TShouldBe>(string name)
    {
        var resourcesYaml = File.ReadAllText(Path.Combine("testassets/resource-informer", name, "resources.yaml"));
        var shouldBeYaml = File.ReadAllText(Path.Combine("testassets/resource-informer", name, "shouldbe.yaml"));

        var resources = ResourceSerializers.DeserializeYaml<TResources>(resourcesYaml);
        var shouldBe = ResourceSerializers.DeserializeYaml<TShouldBe>(shouldBeYaml);

        return (resources, shouldBe);
    }

    private static HttpOperationResponse<KubernetesList<V1Pod>> CreateEmptyPodListResponse()
        => new()
        {
            Body = new KubernetesList<V1Pod>(Array.Empty<V1Pod>(), "v1", V1PodList.KubeKind, new V1ListMeta
            {
                ResourceVersion = "1",
            }),
        };

    private static HttpOperationException CreateHttpOperationException(HttpStatusCode statusCode)
    {
        using var response = new HttpResponseMessage(statusCode);
        return new HttpOperationException($"HTTP {(int)statusCode}")
        {
            Response = new HttpResponseMessageWrapper(response, string.Empty),
        };
    }

    private static void SetupPodListRequest(Mock<ICustomObjectsOperations> customObjects, Action? onRequest = null)
    {
        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
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
            .Callback(() => onRequest?.Invoke())
            .Returns(() => Task.FromResult(CreateEmptyPodListResponse()));
    }

    private static void SetupPodWatchException(Mock<ICustomObjectsOperations> customObjects, Exception exception, Action? onRequest = null)
    {
        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<V1Pod>(
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
                It.Is<bool?>(watch => watch == true),
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => onRequest?.Invoke())
            .ThrowsAsync(exception);
    }

    [Fact]
    public async Task ResourcesAreListedWhenReadyAsyncIsComplete()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, shouldBe) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        var pods = new Dictionary<NamespacedName, V1Pod>();
        var eventTypes = new List<WatchEventType>();

        informer.StartWatching();
        using var registration = informer.Register((eventType, pod) =>
        {
            eventTypes.Add(eventType);
            pods[NamespacedName.From(pod)] = pod;
        });

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration.ReadyAsync(cancellation.Token);

        Assert.Equal(shouldBe, pods.Keys);
        Assert.All(eventTypes, eventType => Assert.Equal(WatchEventType.Added, eventType));
        Assert.Equal(ResourceInformerStatus.Running, informer.Status);
    }

    [Fact]
    public async Task ResourcesWithApiGroupAreListed()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, shouldBe) = LoadTestResource<V1Deployment[], NamespacedName[]>(nameof(ResourcesWithApiGroupAreListed));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Deployment>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = (ResourceInformer<V1Deployment>)testHost.Services.GetRequiredService<IResourceInformer<V1Deployment>>();
        var deployments = new Dictionary<NamespacedName, V1Deployment>();
        var informerRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        informer.StartWatching();
        informer.StartWatching();
        informer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(informer.Status) && informer.Status == ResourceInformerStatus.Running)
            {
                informerRunning.TrySetResult();
            }
        };
        using var registration = informer.Register((eventType, deployment) =>
        {
            deployments[NamespacedName.From(deployment)] = deployment;
        });

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration.ReadyAsync(cancellation.Token);
        await informerRunning.Task.WaitAsync(cancellation.Token);

        Assert.Equal(shouldBe, deployments.Keys);
        Assert.Equal(ResourceInformerStatus.Running, informer.Status);

        var statusChanged = false;
        informer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(informer.Status))
            {
                statusChanged = true;
            }
        };

        informer.StartWatching();

        Assert.Equal(ResourceInformerStatus.Running, informer.Status);
        Assert.False(statusChanged);
    }

    [Fact]
    public void RegisterResourceInformerWithFieldSelectorRegistersSelector()
    {
        var services = new ServiceCollection();
        const string fieldSelector = "metadata.name=my-pod";

        services.RegisterResourceInformer<V1Pod, ResourceInformer<V1Pod>>(fieldSelector);

        using var serviceProvider = services.BuildServiceProvider();

        var selector = serviceProvider.GetRequiredService<ResourceSelector<V1Pod>>();

        Assert.Equal(fieldSelector, selector.FieldSelector);
    }

    [Fact]
    public async Task InformerWaitsForStartWatchingBeforeListing()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, _) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        using var registration = informer.Register((eventType, pod) => { });

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        Assert.Null(clusterHost.Cluster.LastListParameters);

        informer.StartWatching();

        await registration.ReadyAsync(CancellationToken.None).DefaultTimeout();

        Assert.NotNull(clusterHost.Cluster.LastListParameters);
    }

    [Fact]
    public async Task ReadyAsyncCanBeCanceledBeforeInitialList()
    {
        using var cancellation = new CancellationTokenSource();
        using var informer = new ResourceInformer<V1Pod>(
            Mock.Of<IKubernetes>(),
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => informer.ReadyAsync(cancellation.Token));
    }

    [Fact]
    public async Task InitialListRequestsAllPages()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var customObjects = new Mock<ICustomObjectsOperations>();
        customObjects
            .SetupSequence(x => x.ListClusterCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
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
            .ReturnsAsync(new HttpOperationResponse<KubernetesList<V1Pod>>
            {
                Body = new KubernetesList<V1Pod>(
                    [new V1Pod { Metadata = new V1ObjectMeta { Name = "first" } }],
                    "v1",
                    V1PodList.KubeKind,
                    new V1ListMeta { ContinueProperty = "next", ResourceVersion = "1" }),
            })
            .ReturnsAsync(new HttpOperationResponse<KubernetesList<V1Pod>>
            {
                Body = new KubernetesList<V1Pod>(
                    [new V1Pod { Metadata = new V1ObjectMeta { Name = "second" } }],
                    "v1",
                    V1PodList.KubeKind,
                    new V1ListMeta { ResourceVersion = "2" }),
            });

        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<V1Pod>(
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
                It.Is<bool?>(watch => watch == true),
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel())
            .ThrowsAsync(new HttpRequestException("Stop test watch."));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(x => x.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var resources = new List<string>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) => resources.Add(pod.Name())));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(CancellationToken.None).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.Equal(["first", "second"], resources);
        customObjects.Verify(x => x.ListClusterCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
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
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task FailedRelistDoesNotPublishPartialResults()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var customObjects = new Mock<ICustomObjectsOperations>();
        var listRequestCount = 0;

        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
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
            .Returns(() =>
            {
                if (Interlocked.Increment(ref listRequestCount) == 1)
                {
                    return Task.FromResult(new HttpOperationResponse<KubernetesList<V1Pod>>
                    {
                        Body = new KubernetesList<V1Pod>(
                            [new V1Pod { Metadata = new V1ObjectMeta { Name = "committed" } }],
                            "v1",
                            V1PodList.KubeKind,
                            new V1ListMeta { ContinueProperty = "next", ResourceVersion = "1" }),
                    });
                }

                throw new InvalidOperationException("The second list page failed.");
            });

        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<V1Pod>(
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
                It.Is<bool?>(watch => watch == true),
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel())
            .ThrowsAsync(new HttpRequestException("Stop test watch."));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(x => x.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            resourceListLimit: 1);
        var events = new List<(WatchEventType Type, string Name)>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            events.Add((eventType, pod.Name()))));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => runTask);

        Assert.Empty(events);
        Assert.Equal(ResourceInformerStatus.Faulted, informer.Status);
    }

    [Fact]
    public async Task NamespacedInformerUsesNamespacedListEndpoint()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var customObjects = new Mock<ICustomObjectsOperations>();

        customObjects
            .Setup(x => x.ListNamespacedCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "test-namespace",
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
            .ReturnsAsync(new HttpOperationResponse<KubernetesList<V1Pod>>
            {
                Body = new KubernetesList<V1Pod>(Array.Empty<V1Pod>(), "v1", V1PodList.KubeKind, new V1ListMeta
                {
                    ResourceVersion = "1",
                }),
            });

        customObjects
            .Setup(x => x.ListNamespacedCustomObjectWithHttpMessagesAsync<V1Pod>(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "test-namespace",
                It.IsAny<string>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.Is<bool?>(watch => watch == true),
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel())
            .ThrowsAsync(new HttpRequestException("Stop test watch."));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(x => x.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            @namespace: "test-namespace");
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) => { }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(CancellationToken.None).DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        customObjects.Verify(x => x.ListNamespacedCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
            It.IsAny<string>(),
            It.IsAny<string>(),
            "test-namespace",
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
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WatchRequestsIncludeBookmarks()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, _) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        using var registration = informer.Register((eventType, pod) => { });

        informer.StartWatching();

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        var watchRequest = await clusterHost.Cluster.WatchRequested.DefaultTimeout();

        Assert.True(watchRequest.Watch);
        Assert.True(watchRequest.AllowWatchBookmarks);
    }

    [Fact]
    public async Task WatchEventsNotifyRegistrations()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "ADDED",
            Object = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "pod", ResourceVersion = "1" },
            },
        });
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "MODIFIED",
            Object = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "pod", ResourceVersion = "2" },
            },
        });
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "DELETED",
            Object = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "pod", ResourceVersion = "3" },
            },
        });

        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var eventTypes = new List<WatchEventType>();
        var eventsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            eventTypes.Add(eventType);
            if (eventTypes.Count == 3)
            {
                eventsReceived.TrySetResult();
                cancellation.Cancel();
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await eventsReceived.Task.DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.Equal(
            [WatchEventType.Added, WatchEventType.Modified, WatchEventType.Deleted],
            eventTypes);
        Assert.Empty(clusterHost.Cluster.WatchEvents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostedInformerPublishesDeletedWatchEventsWithConfiguredStartBehavior(bool startWatchingOnHostStart)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "DELETED",
            Object = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "pod", NamespaceProperty = "default", ResourceVersion = "1" },
            },
        });

        await clusterHost.StartAsync(cancellation.Token);

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>(startWatchingOnHostStart);
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        var deletedPod = new TaskCompletionSource<V1Pod>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = informer.Register((eventType, pod) =>
        {
            if (eventType == WatchEventType.Deleted)
            {
                deletedPod.TrySetResult(pod);
            }
        });

        await testHost.StartAsync(cancellation.Token);
        if (!startWatchingOnHostStart)
        {
            informer.StartWatching();
        }

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        var deleted = await deletedPod.Task.DefaultTimeout();
        Assert.Equal("pod", deleted.Metadata?.Name);
        Assert.Equal("default", deleted.Metadata?.NamespaceProperty);
    }

    [Fact]
    public async Task MultipleRegistrationsReceiveTheSameInitialEvents()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, shouldBe) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        var received1 = new Dictionary<NamespacedName, V1Pod>();
        var received2 = new Dictionary<NamespacedName, V1Pod>();

        informer.StartWatching();
        using var registration1 = informer.Register((eventType, pod) => received1[NamespacedName.From(pod)] = pod);
        using var registration2 = informer.Register((eventType, pod) => received2[NamespacedName.From(pod)] = pod);

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration1.ReadyAsync(cancellation.Token).DefaultTimeout();
        await registration2.ReadyAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(shouldBe, received1.Keys);
        Assert.Equal(shouldBe, received2.Keys);
    }

    [Fact]
    public async Task DisposedRegistrationStopsReceivingEvents()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, shouldBe) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        var received1 = new Dictionary<NamespacedName, V1Pod>();
        var received2 = new Dictionary<NamespacedName, V1Pod>();

        using var registration1 = informer.Register((eventType, pod) => received1[NamespacedName.From(pod)] = pod);
        using var registration2 = informer.Register((eventType, pod) => received2[NamespacedName.From(pod)] = pod);

        registration1.Dispose();

        informer.StartWatching();

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration2.ReadyAsync(cancellation.Token).DefaultTimeout();

        Assert.Empty(received1);
        Assert.Equal(shouldBe, received2.Keys);
    }

    [Fact]
    public async Task StoppingTheHostStopsTheInformer()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, _) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        using var registration = informer.Register((eventType, pod) => { });

        informer.StartWatching();

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        await testHost.StopAsync(cancellation.Token);

        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }

    [Fact]
    public async Task CallbackExceptionsFaultTheInformerRunLoop()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, _) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = (ResourceInformer<V1Pod>)testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        using var registration = informer.Register((WatchEventType eventType, V1Pod pod) => throw new ApplicationException("boom"));

        await clusterHost.StartAsync(cancellation.Token);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => runTask);

        Assert.Contains(ex.InnerExceptions, inner => inner is ApplicationException && inner.Message == "boom");
        Assert.Equal(ResourceInformerStatus.Faulted, informer.Status);
    }

    [Fact]
    public async Task HttpRequestExceptionDuringWatchDoesNotFaultTheInformer()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var watchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var customObjects = new Mock<ICustomObjectsOperations>();
        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
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
            .ReturnsAsync(new HttpOperationResponse<KubernetesList<V1Pod>>()
            {
                Body = new KubernetesList<V1Pod>(Array.Empty<V1Pod>(), "v1", V1PodList.KubeKind, new V1ListMeta
                {
                    ResourceVersion = "1",
                }),
            });

        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<V1Pod>(
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
                It.Is<bool?>(watch => watch == true),
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => watchRequested.TrySetResult())
            .ThrowsAsync(new HttpRequestException("Error while copying content to a stream.", new EndOfStreamException("Attempted to read past the end of the stream.")));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(x => x.CustomObjects).Returns(customObjects.Object);

        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var informer = new ResourceInformer<V1Pod>(kubernetes.Object, Mock.Of<IHostApplicationLifetime>(), loggerFactory.CreateLogger<ResourceInformer<V1Pod>>());
        var runTask = informer.RunAsync(cancellation.Token);

        informer.StartWatching();

        await watchRequested.Task.DefaultTimeout();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }

    [Fact]
    public async Task ExpiredWatchResourceVersionTriggersRelist()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var secondListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;

        var customObjects = new Mock<ICustomObjectsOperations>();
        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
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
            .Callback(() =>
            {
                if (Interlocked.Increment(ref listRequestCount) == 2)
                {
                    secondListRequested.TrySetResult();
                }
            })
            .ReturnsAsync(new HttpOperationResponse<KubernetesList<V1Pod>>
            {
                Body = new KubernetesList<V1Pod>(Array.Empty<V1Pod>(), "v1", V1PodList.KubeKind, new V1ListMeta
                {
                    ResourceVersion = "1",
                }),
            });

        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<V1Pod>(
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
                It.Is<bool?>(watch => watch == true),
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KubernetesException(new V1Status { Reason = "Expired" }));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(x => x.CustomObjects).Returns(customObjects.Object);

        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var informer = new ResourceInformer<V1Pod>(kubernetes.Object, Mock.Of<IHostApplicationLifetime>(), loggerFactory.CreateLogger<ResourceInformer<V1Pod>>());
        var runTask = informer.RunAsync(cancellation.Token);

        informer.StartWatching();

        await secondListRequested.Task.WaitAsync(CancellationToken.None).DefaultTimeout();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(listRequestCount >= 2, $"Expected at least 2 list requests, saw {listRequestCount}.");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpOperationExceptionDuringListRetriesForTransientStatus(HttpStatusCode statusCode)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var watchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;

        var customObjects = new Mock<ICustomObjectsOperations>();
        customObjects
            .Setup(x => x.ListClusterCustomObjectWithHttpMessagesAsync<KubernetesList<V1Pod>>(
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
            .Callback(() => Interlocked.Increment(ref listRequestCount))
            .Returns(() => Volatile.Read(ref listRequestCount) == 1
                ? Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(CreateHttpOperationException(statusCode))
                : Task.FromResult(CreateEmptyPodListResponse()));

        SetupPodWatchException(customObjects, new HttpRequestException("End watch after initial list."), () => watchRequested.TrySetResult());

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await watchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(listRequestCount >= 2, $"Expected the list request to retry, saw {listRequestCount} request(s).");
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpOperationExceptionDuringWatchRetriesForTransientStatus(HttpStatusCode statusCode)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects);
        SetupPodWatchException(customObjects, CreateHttpOperationException(statusCode), () =>
        {
            if (Interlocked.Increment(ref watchRequestCount) == 2)
            {
                secondWatchRequested.TrySetResult();
            }
        });

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }

    [Fact]
    public async Task HttpGoneDuringWatchTriggersRelist()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var secondListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects, () =>
        {
            if (Interlocked.Increment(ref listRequestCount) == 2)
            {
                secondListRequested.TrySetResult();
            }
        });
        SetupPodWatchException(customObjects, CreateHttpOperationException(HttpStatusCode.Gone));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await secondListRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(listRequestCount >= 2, $"Expected a relist after HTTP 410, saw {listRequestCount} request(s).");
    }

    [Fact]
    public async Task NonRetryableHttpOperationExceptionFaultsInformer()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects);
        SetupPodWatchException(customObjects, CreateHttpOperationException(HttpStatusCode.Forbidden));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await Assert.ThrowsAsync<HttpOperationException>(() => runTask);

        Assert.Equal(ResourceInformerStatus.Faulted, informer.Status);
    }

    [Fact]
    public async Task RunInfiniteStartsTheInformer()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, _) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();

        using var testHost = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.RegisterResourceInformer<V1Pod>();
                services.Configure<KubernetesClientOptions>(options =>
                {
                    options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
                });
                services.AddKubernetesCore();
            })
            .Build();

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        using var registration = informer.Register((eventType, pod) => { });

        await clusterHost.StartAsync(cancellation.Token);

        var runTask = informer.RunInfinite(cancellation.Token);

        await clusterHost.Cluster.ListRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        cancellation.Cancel();
        await runTask;

        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }
}
