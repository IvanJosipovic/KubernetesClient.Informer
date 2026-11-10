// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

    private static void SetupPodListRequest(
        Mock<ICustomObjectsOperations> customObjects,
        Action? onRequest = null,
        Func<HttpOperationResponse<KubernetesList<V1Pod>>>? responseFactory = null,
        List<string?>? requestedResourceVersions = null,
        Func<Task<HttpOperationResponse<KubernetesList<V1Pod>>>>? responseFactoryAsync = null)
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
            .Callback(new InvocationAction(invocation =>
            {
                requestedResourceVersions?.Add(invocation.Arguments[8] as string);
                onRequest?.Invoke();
            }))
            .Returns(() => responseFactoryAsync?.Invoke()
                ?? Task.FromResult(responseFactory?.Invoke() ?? CreateEmptyPodListResponse()));
    }

    private static HttpOperationResponse<KubernetesList<V1Pod>> CreatePodListResponse(
        IList<V1Pod> pods,
        string resourceVersion = "1",
        string? continueParameter = null)
        => new()
        {
            Body = new KubernetesList<V1Pod>(pods, "v1", V1PodList.KubeKind, new V1ListMeta
            {
                ResourceVersion = resourceVersion,
                ContinueProperty = continueParameter,
            }),
        };

    private static void DispatchWatchEvent(ResourceInformer<V1Pod> informer, WatchEventType eventType, string name, string resourceVersion)
    {
        var onEvent = typeof(ResourceInformer<V1Pod>).GetMethod("OnEvent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEvent);
        onEvent.Invoke(informer, [eventType, new V1Pod
        {
            Kind = "Pod",
            ApiVersion = "v1",
            Metadata = new V1ObjectMeta { Name = name, ResourceVersion = resourceVersion },
        }]);
    }

    private static void SetupPodWatchException(
        Mock<ICustomObjectsOperations> customObjects,
        Exception exception,
        Action? onRequest = null,
        List<string?>? requestedResourceVersions = null)
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
            .Callback(new InvocationAction(invocation =>
            {
                requestedResourceVersions?.Add(invocation.Arguments[8] as string);
                onRequest?.Invoke();
            }))
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
        var releaseWatchResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clusterHost.Cluster.WatchResponseGate = releaseWatchResponse.Task;

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

        try
        {
            await registration.ReadyAsync(cancellation.Token);
            await informerRunning.Task.WaitAsync(cancellation.Token);
            await clusterHost.Cluster.WaitForWatchRequestAsync(1).WaitAsync(cancellation.Token).DefaultTimeout();
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
        finally
        {
            releaseWatchResponse.TrySetResult();
        }

        Assert.Equal(shouldBe, deployments.Keys);
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
    public async Task RegisterAcceptsInterfaceTypedCallback()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pod = new V1Pod { Metadata = new V1ObjectMeta { Name = "interface-callback" } };
        var callbackReceived = new TaskCompletionSource<(WatchEventType EventType, string? Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects, responseFactory: () => CreatePodListResponse([pod]));
        SetupPodWatchException(customObjects, new HttpRequestException("Stop the test watch."));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        ResourceInformerCallback<IKubernetesObject<V1ObjectMeta>> callback = (eventType, resource) =>
            callbackReceived.TrySetResult((eventType, resource.Metadata.Name));
        using var registration = informer.Register(callback);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal((WatchEventType.Added, "interface-callback"), await callbackReceived.Task.WaitAsync(cancellation.Token).DefaultTimeout());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ConfiguredFieldSelectorIsSentWithTheListRequest()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            selector: new ResourceSelector<V1Pod>("metadata.name=selected-pod"));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.ListRequested.WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal("metadata.name=selected-pod", clusterHost.Cluster.ListRequests.Single().FieldSelector);
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal("metadata.name=selected-pod", clusterHost.Cluster.WatchRequests.Single().FieldSelector);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
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
                It.Is<string?>(resourceVersion => resourceVersion == "0"),
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
        var listRequests = customObjects.Invocations
            .Where(invocation => invocation.Method.Name == nameof(ICustomObjectsOperations.ListClusterCustomObjectWithHttpMessagesAsync)
                && invocation.Method.GetGenericArguments().Single() == typeof(KubernetesList<V1Pod>))
            .ToArray();
        Assert.Equal(2, listRequests.Length);
        Assert.Equal(new string?[] { null, "next" }, listRequests.Select(invocation => invocation.Arguments[4] as string));
        Assert.Equal(new string?[] { "0", "0" }, listRequests.Select(invocation => invocation.Arguments[8] as string));
        Assert.All(listRequests, invocation => Assert.Equal(1000, invocation.Arguments[7]));
    }

    [Fact]
    public async Task TestClusterScriptCanDrivePaginatedListAndWatchResourceVersion()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "10",
            Continue = "next-page",
            Items = [new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "first" },
            }],
        });
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "11",
            Continue = null!,
            Items = [new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "second" },
            }],
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            resourceListLimit: 1);
        var received = new List<(WatchEventType EventType, string Name)>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            received.Add((eventType, pod.Name()))));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        var watchRequest = await clusterHost.Cluster.WaitForWatchRequestAsync(1).WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(new string?[] { null, "next-page" }, clusterHost.Cluster.ListRequests.Select(request => request.Continue));
        Assert.All(clusterHost.Cluster.ListRequests, request => Assert.Equal(1, request.Limit));
        Assert.Equal("11", watchRequest.ResourceVersion);
        Assert.Equal([(WatchEventType.Added, "first"), (WatchEventType.Added, "second")], received);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task FailedInitialListPageRetriesWithoutPublishingPartialResults()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timeProvider = new TestTimeProvider();
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
                var request = Interlocked.Increment(ref listRequestCount);
                if (request == 1)
                {
                    return Task.FromResult(new HttpOperationResponse<KubernetesList<V1Pod>>
                    {
                        Body = new KubernetesList<V1Pod>(
                            [new V1Pod { Metadata = new V1ObjectMeta { Name = "partial" } }],
                            "v1",
                            V1PodList.KubeKind,
                            new V1ListMeta { ContinueProperty = "next", ResourceVersion = "1" }),
                    });
                }

                if (request == 2)
                {
                    throw new InvalidOperationException("The second list page failed.");
                }

                if (request == 3)
                {
                    return Task.FromResult(CreatePodListResponse(
                        [new V1Pod { Metadata = new V1ObjectMeta { Name = "recovered" } }],
                        "2"));
                }

                throw new InvalidOperationException("Unexpected list request.");
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
            resourceListLimit: 1,
            timeProvider: timeProvider);
        var events = new List<(WatchEventType Type, string Name)>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            events.Add((eventType, pod.Name()))));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(0);
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.Equal([(WatchEventType.Added, "recovered")], events);
        Assert.Equal(3, listRequestCount);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
        var listRequests = customObjects.Invocations
            .Where(invocation => invocation.Method.Name == nameof(ICustomObjectsOperations.ListClusterCustomObjectWithHttpMessagesAsync)
                && invocation.Method.GetGenericArguments().Single() == typeof(KubernetesList<V1Pod>))
            .ToArray();
        Assert.Equal(new string?[] { null, "next", null }, listRequests.Select(invocation => invocation.Arguments[4] as string));
        Assert.All(listRequests, invocation => Assert.Equal(1, invocation.Arguments[7]));
    }

    [Fact]
    public async Task FailedRelistPageRetriesWithoutPublishingPartialResultsOrReplacingCache()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var relistPageFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relistEventsDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;
        var callbackCount = 0;
        var existing = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "existing",
                NamespaceProperty = "default",
                ResourceVersion = "1",
                Labels = new Dictionary<string, string> { ["generation"] = "old" },
            },
        };
        var removed = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "removed",
                NamespaceProperty = "default",
                ResourceVersion = "1",
            },
        };
        var updatedExisting = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "existing",
                NamespaceProperty = "default",
                ResourceVersion = "2",
                Labels = new Dictionary<string, string> { ["generation"] = "new" },
            },
        };
        var added = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "added",
                NamespaceProperty = "default",
                ResourceVersion = "2",
            },
        };

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () =>
            {
                if (Interlocked.Increment(ref listRequestCount) == 4)
                {
                    relistPageFailed.TrySetResult();
                }
            },
            responseFactoryAsync: () => Volatile.Read(ref listRequestCount) switch
            {
                1 => Task.FromResult(CreatePodListResponse([existing], "1", "initial-next")),
                2 => Task.FromResult(CreatePodListResponse([removed], "1")),
                3 => Task.FromResult(CreatePodListResponse([updatedExisting], "2", "relist-next")),
                4 => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(
                    new InvalidOperationException("The second relist page failed.")),
                5 => Task.FromResult(CreatePodListResponse([updatedExisting], "2", "relist-next")),
                6 => Task.FromResult(CreatePodListResponse([added], "2")),
                _ => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(
                    new InvalidOperationException("Unexpected list request.")),
            });
        SetupPodWatchException(customObjects, CreateHttpOperationException(HttpStatusCode.Gone));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            resourceListLimit: 1,
            timeProvider: timeProvider);
        var received = new ConcurrentQueue<(WatchEventType EventType, V1Pod Resource)>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            received.Enqueue((eventType, pod));
            if (Interlocked.Increment(ref callbackCount) == 5)
            {
                relistEventsDelivered.TrySetResult();
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(
            [(WatchEventType.Added, "existing"), (WatchEventType.Added, "removed")],
            received.Select(item => (item.EventType, item.Resource.Name())).ToArray());

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await relistPageFailed.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        await timeProvider.WaitForTimerCountAsync(3, cancellation.Token).DefaultTimeout();

        Assert.Equal(ResourceInformerStatus.Retrying, informer.Status);
        Assert.Equal(
            [(WatchEventType.Added, "existing"), (WatchEventType.Added, "removed")],
            received.Select(item => (item.EventType, item.Resource.Name())).ToArray());

        var cachedAfterFailedRelist = new ConcurrentQueue<(WatchEventType EventType, string Name)>();
        using var cacheVerifier = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            cachedAfterFailedRelist.Enqueue((eventType, pod.Name()))));
        await cacheVerifier.ReadyAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(
            [(WatchEventType.Added, "existing"), (WatchEventType.Added, "removed")],
            cachedAfterFailedRelist.ToArray());

        timeProvider.FireTimer(2);
        await relistEventsDelivered.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        var events = received.ToArray();
        Assert.Equal(
            [
                (WatchEventType.Added, "existing"),
                (WatchEventType.Added, "removed"),
                (WatchEventType.Modified, "existing"),
                (WatchEventType.Added, "added"),
                (WatchEventType.Deleted, "removed"),
            ],
            events.Select(item => (item.EventType, item.Resource.Name())).ToArray());
        Assert.Equal("old", events[0].Resource.Metadata.Labels["generation"]);
        Assert.Equal("new", events[2].Resource.Metadata.Labels["generation"]);
        Assert.Same(removed, events[4].Resource);
        Assert.Equal(6, listRequestCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
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
    public async Task BookmarkAdvancesNextWatchResourceVersionWithoutNotifyingRegistrations()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "BOOKMARK",
            Object = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { ResourceVersion = "5" },
            },
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var callbackCount = 0;
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => Interlocked.Increment(ref callbackCount)));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        var secondWatch = await clusterHost.Cluster.WaitForWatchRequestAsync(2).WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal("5", secondWatch.ResourceVersion);
        Assert.Single(clusterHost.Cluster.ListRequests);
        Assert.Equal(0, Volatile.Read(ref callbackCount));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task WatchEventAdvancesResourceVersionOnReconnectWithoutRelisting()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "MODIFIED",
            Object = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "updated", ResourceVersion = "5" },
            },
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var modifiedEvent = new TaskCompletionSource<V1Pod>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            if (eventType == WatchEventType.Modified)
            {
                modifiedEvent.TrySetResult(pod);
            }
        }));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var secondWatch = await clusterHost.Cluster.WaitForWatchRequestAsync(2)
            .WaitAsync(cancellation.Token).DefaultTimeout();
        var modified = await modifiedEvent.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal("5", secondWatch.ResourceVersion);
        Assert.Equal("updated", modified.Name());
        Assert.Single(clusterHost.Cluster.ListRequests);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ConfiguredTimeoutIsSentForListAndWatchRequests()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeoutSeconds: 42);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.ListRequested.WaitAsync(cancellation.Token).DefaultTimeout();
        var watchRequest = await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(42, clusterHost.Cluster.ListRequests.Single().TimeoutSeconds);
        Assert.Equal(42, watchRequest.TimeoutSeconds);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ExpiredWatchErrorEventTriggersRelist()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (resources, _) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));
        using var clusterHost = new TestClusterHostBuilder().UseInitialResources(resources).Build();
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
                    ["message"] = "resource version expired",
                    ["reason"] = "Expired",
                    ["code"] = 410,
                },
            },
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.WaitForWatchRequestAsync(2).WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(2, clusterHost.Cluster.ListRequests.Count);
        Assert.Equal("0", clusterHost.Cluster.ListRequests[0].ResourceVersion);
        Assert.Equal("0", clusterHost.Cluster.ListRequests[1].ResourceVersion);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task IdleWatchTimeoutReconnectsWithoutRelisting()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await clusterHost.StartAsync(cancellation.Token);

        var timeProvider = new TestTimeProvider();
        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.WaitForWatchRequestAsync(1).WaitAsync(cancellation.Token).DefaultTimeout();
        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        timeProvider.FireTimer(0);

        await clusterHost.Cluster.WaitForWatchRequestAsync(2).WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Single(clusterHost.Cluster.ListRequests);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
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
    public async Task CallbackExceptionRemovesOnlyTheFailingRegistration()
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
        var healthyCallbackCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var healthyRegistration = informer.Register((WatchEventType eventType, V1Pod pod) => healthyCallbackCalled.TrySetResult());

        await clusterHost.StartAsync(cancellation.Token);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await healthyRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await healthyCallbackCalled.Task.DefaultTimeout();

        var ex = await Assert.ThrowsAsync<ApplicationException>(() => registration.ReadyAsync(cancellation.Token));
        Assert.Equal("boom", ex.Message);
        Assert.False(runTask.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
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

    [Theory]
    [InlineData((int)HttpStatusCode.Gone, "Gone")]
    [InlineData(0, "Expired")]
    public async Task ExpiredWatchResourceVersionTriggersRelist(int statusCode, string reason)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var timeProvider = new TestTimeProvider();

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
            .ThrowsAsync(new KubernetesException(new V1Status
            {
                Code = statusCode,
                Reason = reason,
            }));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(x => x.CustomObjects).Returns(customObjects.Object);

        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            loggerFactory.CreateLogger<ResourceInformer<V1Pod>>(),
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);

        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondListRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(listRequestCount >= 2, $"Expected at least 2 list requests, saw {listRequestCount}.");
    }

    [Theory]
    [InlineData("2")]
    [InlineData("Thu, 01 Jan 1970 00:00:02 GMT")]
    public async Task ListRetryHonorsRetryAfterHeader(string retryAfterValue)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var watchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;
        var retryException = CreateHttpOperationException(HttpStatusCode.TooManyRequests);
        retryException.Response!.Headers["Retry-After"] = [retryAfterValue];

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () => Interlocked.Increment(ref listRequestCount),
            () =>
            {
                if (Volatile.Read(ref listRequestCount) == 1)
                {
                    throw retryException;
                }

                return CreateEmptyPodListResponse();
            });
        SetupPodWatchException(customObjects, new HttpRequestException("End watch after the list retry."), () => watchRequested.TrySetResult());

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, listRequestCount);
        timeProvider.FireTimer(0);

        await watchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(2, listRequestCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("Thu, 01 Jan 1970 00:00:03 GMT")]
    public async Task WatchRetryHonorsRetryAfterHeader(string retryAfterValue)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        var requestedResourceVersions = new List<string?>();
        var retryException = CreateHttpOperationException(HttpStatusCode.TooManyRequests);
        retryException.Response!.Headers["Retry-After"] = [retryAfterValue];

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects);
        SetupPodWatchException(
            customObjects,
            retryException,
            () =>
            {
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }
            },
            requestedResourceVersions);
        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(1, TimeSpan.FromSeconds(3));
        timeProvider.FireTimer(1);
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(2, watchRequestCount);
        Assert.Equal(["1", "1"], requestedResourceVersions);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ListRetryBackoffIncreasesAcrossConsecutiveFailures()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var watchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;
        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () => Interlocked.Increment(ref listRequestCount),
            responseFactoryAsync: () => Volatile.Read(ref listRequestCount) switch
            {
                <= 3 => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(
                    CreateHttpOperationException(HttpStatusCode.InternalServerError)),
                4 => Task.FromResult(CreateEmptyPodListResponse()),
                _ => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(
                    new InvalidOperationException("Unexpected list request.")),
            });
        SetupPodWatchException(customObjects, new HttpRequestException("Stop after retry progression."), () => watchRequested.TrySetResult());
        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        Assert.InRange(timeProvider.GetTimerDueTime(0), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));
        timeProvider.FireTimer(0);

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        Assert.InRange(timeProvider.GetTimerDueTime(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        timeProvider.FireTimer(1);

        await timeProvider.WaitForTimerCountAsync(3, cancellation.Token).DefaultTimeout();
        Assert.InRange(timeProvider.GetTimerDueTime(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6));
        timeProvider.FireTimer(2);
        await watchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(4, listRequestCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task WatchRetryBackoffIncreasesAcrossShortLivedFailures()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var watchRequests = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var watchRequestCount = 0;
        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects);
        SetupPodWatchException(
            customObjects,
            new HttpRequestException("The watch failed quickly."),
            () =>
            {
                var request = Interlocked.Increment(ref watchRequestCount);
                if (request <= watchRequests.Length)
                {
                    watchRequests[request - 1].TrySetResult();
                }
            });

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var retryTimerIndices = new[] { 1, 3, 5 };
        var minimumSeconds = new[] { 0.5, 1, 2 };
        var maximumSeconds = new[] { 1.5, 3, 6 };
        for (var failure = 0; failure < retryTimerIndices.Length; failure++)
        {
            await watchRequests[failure].Task.WaitAsync(cancellation.Token).DefaultTimeout();
            await timeProvider.WaitForTimerCountAsync((failure + 1) * 2, cancellation.Token).DefaultTimeout();

            var retryTimerIndex = retryTimerIndices[failure];
            Assert.InRange(
                timeProvider.GetTimerDueTime(retryTimerIndex),
                TimeSpan.FromSeconds(minimumSeconds[failure]),
                TimeSpan.FromSeconds(maximumSeconds[failure]));

            if (failure < retryTimerIndices.Length - 1)
            {
                timeProvider.FireTimer(retryTimerIndex);
            }
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task WatchRetryBackoffResetsAfterSustainedWatch()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var watchRequests = Enumerable.Range(0, 5)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var sustainedWatchFailure = new TaskCompletionSource<HttpOperationResponse<V1Pod>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects);
        customObjects
            .Setup(client => client.ListClusterCustomObjectWithHttpMessagesAsync<V1Pod>(
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
            .Callback(() =>
            {
                var request = Interlocked.Increment(ref watchRequestCount);
                if (request <= watchRequests.Length)
                {
                    watchRequests[request - 1].TrySetResult();
                }
            })
            .Returns(() => Volatile.Read(ref watchRequestCount) == watchRequests.Length
                ? sustainedWatchFailure.Task
                : Task.FromException<HttpOperationResponse<V1Pod>>(
                    new HttpRequestException("The watch failed quickly.")));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var retryTimerIndices = new[] { 1, 3, 5, 7 };
        var minimumSeconds = new[] { 0.5, 1, 2, 4 };
        var maximumSeconds = new[] { 1.5, 3, 6, 12 };
        for (var failure = 0; failure < retryTimerIndices.Length; failure++)
        {
            await watchRequests[failure].Task.WaitAsync(cancellation.Token).DefaultTimeout();
            await timeProvider.WaitForTimerCountAsync((failure + 1) * 2, cancellation.Token).DefaultTimeout();

            var retryTimerIndex = retryTimerIndices[failure];
            Assert.InRange(
                timeProvider.GetTimerDueTime(retryTimerIndex),
                TimeSpan.FromSeconds(minimumSeconds[failure]),
                TimeSpan.FromSeconds(maximumSeconds[failure]));
            timeProvider.FireTimer(retryTimerIndex);
        }

        await watchRequests[4].Task.WaitAsync(cancellation.Token).DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        sustainedWatchFailure.TrySetException(new HttpRequestException("The sustained watch failed."));
        await timeProvider.WaitForTimerCountAsync(10, cancellation.Token).DefaultTimeout();

        Assert.InRange(
            timeProvider.GetTimerDueTime(9),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(1500));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task CallbackExceptionOnLaterWatchEventRemovesOnlyFailingRegistration()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await clusterHost.StartAsync(cancellation.Token);

        var logger = new CallbackFailureObserverLogger();
        using var informer = new ResourceInformer<V1Pod>(clusterHost.Client, Mock.Of<IHostApplicationLifetime>(), logger);
        var failingCallbackEvents = new ConcurrentQueue<string>();
        var healthyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failingRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, pod) =>
        {
            failingCallbackEvents.Enqueue(pod.Name());
            throw new InvalidOperationException("Failure on a watch notification.");
        }));
        using var healthyRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, pod) =>
        {
            if (pod.Name() == "after-failure")
            {
                healthyReceived.TrySetResult();
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();
        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await failingRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await healthyRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        DispatchWatchEvent(informer, WatchEventType.Modified, "failure", "1");
        await logger.CallbackFailureLogged.WaitAsync(cancellation.Token).DefaultTimeout();
        DispatchWatchEvent(informer, WatchEventType.Added, "after-failure", "2");
        await healthyReceived.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(["failure"], failingCallbackEvents.ToArray());
        Assert.False(runTask.IsCompleted);
        Assert.Equal(111, logger.CallbackFailureEventId.Id);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task CallbackIsUnregisteredBeforeSlowFailureLoggingCompletes()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await clusterHost.StartAsync(cancellation.Token);

        var logger = new BlockingCallbackFailureLogger();
        using var informer = new ResourceInformer<V1Pod>(clusterHost.Client, Mock.Of<IHostApplicationLifetime>(), logger);
        var failingCallbackCount = 0;
        var healthyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failingRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) =>
        {
            Interlocked.Increment(ref failingCallbackCount);
            throw new InvalidOperationException("Block while logging callback failure.");
        }));
        using var healthyRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, pod) =>
        {
            if (pod.Name() == "while-logging")
            {
                healthyReceived.TrySetResult();
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();
        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await failingRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await healthyRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        DispatchWatchEvent(informer, WatchEventType.Modified, "first-failure", "1");
        await logger.LogEntered.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        try
        {
            var registrationsField = typeof(ResourceInformer<V1Pod>).GetField("_registrations", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(registrationsField);
            var activeRegistrations = ((System.Collections.IEnumerable)registrationsField.GetValue(informer)!).Cast<object>();
            Assert.DoesNotContain(failingRegistration, activeRegistrations);

            DispatchWatchEvent(informer, WatchEventType.Added, "while-logging", "2");
            await healthyReceived.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        }
        finally
        {
            logger.ReleaseLog();
        }

        await logger.LogCompleted.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(1, Volatile.Read(ref failingCallbackCount));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task LateRegistrationCallbackFailureFaultsOnlyItsReadiness()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources([new V1Pod { Metadata = new V1ObjectMeta { Name = "cached" } }])
            .Build();
        clusterHost.Cluster.WatchResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var healthyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var healthyRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, pod) =>
        {
            if (pod.Name() == "after-late-failure")
            {
                healthyReceived.TrySetResult();
            }
        }));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await healthyRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        var lateCallbackCount = 0;
        using var failingRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) =>
        {
            Interlocked.Increment(ref lateCallbackCount);
            throw new InvalidOperationException("Late cached replay failed.");
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingRegistration.ReadyAsync(cancellation.Token).DefaultTimeout());
        Assert.Equal("Late cached replay failed.", error.Message);

        DispatchWatchEvent(informer, WatchEventType.Modified, "after-late-failure", "2");
        await healthyReceived.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(1, Volatile.Read(ref lateCallbackCount));
        Assert.False(runTask.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task CancelingRegistrationReadinessWaitDoesNotCancelTheRegistration()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var readinessCancellation = new CancellationTokenSource();
        var listRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseListRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () => listRequestStarted.TrySetResult(),
            responseFactoryAsync: async () =>
            {
                await releaseListRequest.Task;
                return CreateEmptyPodListResponse();
            });
        SetupPodWatchException(customObjects, new HttpRequestException("Stop after readiness is verified."));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => { }));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await listRequestStarted.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        var canceledWait = registration.ReadyAsync(readinessCancellation.Token);
        readinessCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);

        releaseListRequest.TrySetResult();
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(400)]
    public async Task KubernetesStatusRetryAfterSecondsControlsOrFallsBackForListRetry(int retryAfterSeconds)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var listRequestCount = 0;
        var watchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryException = new KubernetesException(new V1Status
        {
            Code = (int)HttpStatusCode.TooManyRequests,
            Reason = "TooManyRequests",
            Details = new V1StatusDetails { RetryAfterSeconds = retryAfterSeconds },
        });

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () => Interlocked.Increment(ref listRequestCount),
            responseFactoryAsync: () => Volatile.Read(ref listRequestCount) == 1
                ? Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(retryException)
                : Task.FromResult(CreateEmptyPodListResponse()));
        SetupPodWatchException(customObjects, new HttpRequestException("Stop the test watch."), () => watchRequested.TrySetResult());

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        var retryDelay = timeProvider.GetTimerDueTime(0);
        if (retryAfterSeconds is > 0 and <= 300)
        {
            Assert.Equal(TimeSpan.FromSeconds(retryAfterSeconds), retryDelay);
        }
        else if (retryAfterSeconds > 300)
        {
            Assert.Equal(TimeSpan.FromSeconds(300), retryDelay);
        }
        else
        {
            Assert.InRange(retryDelay, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));
        }

        Assert.Equal(1, listRequestCount);
        timeProvider.FireTimer(0);
        await watchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(2, listRequestCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Theory]
    [InlineData("400")]
    [InlineData("not-a-date")]
    [InlineData("Wed, 21 Oct 2015 07:28:00 GMT")]
    public async Task ListRetryBoundsOrIgnoresInvalidRetryAfterValues(string retryAfterValue)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var listRequestCount = 0;
        var retryException = CreateHttpOperationException(HttpStatusCode.TooManyRequests);
        retryException.Response!.Headers["Retry-After"] = [retryAfterValue];

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () => Interlocked.Increment(ref listRequestCount),
            () => Volatile.Read(ref listRequestCount) == 1
                ? throw retryException
                : CreateEmptyPodListResponse());
        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        var retryDelay = timeProvider.GetTimerDueTime(0);
        if (retryAfterValue == "400")
        {
            Assert.Equal(TimeSpan.FromSeconds(300), retryDelay);
        }
        else
        {
            Assert.InRange(retryDelay, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500));
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpOperationExceptionDuringListRetriesForTransientStatus(HttpStatusCode statusCode)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
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
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(0);
        await watchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(2, listRequestCount);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpOperationExceptionDuringWatchRetriesForTransientStatus(HttpStatusCode statusCode)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        var listRequestCount = 0;
        var requestedWatchResourceVersions = new List<string?>();

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects, () => Interlocked.Increment(ref listRequestCount));
        SetupPodWatchException(
            customObjects,
            CreateHttpOperationException(statusCode),
            () =>
            {
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }
            },
            requestedWatchResourceVersions);

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
        Assert.Equal(1, listRequestCount);
        Assert.Equal(["1", "1"], requestedWatchResourceVersions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchRetriesSocketAndIndependentTimeoutFailures(bool useTaskCanceledException)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        var listRequestCount = 0;
        var requestedResourceVersions = new List<string?>();
        Exception watchException = useTaskCanceledException
            ? new TaskCanceledException("The watch request timed out independently.")
            : new IOException("The socket was reset.", new SocketException((int)SocketError.ConnectionReset));

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects, () => Interlocked.Increment(ref listRequestCount));
        SetupPodWatchException(customObjects, watchException, () =>
        {
            if (Interlocked.Increment(ref watchRequestCount) == 2)
            {
                secondWatchRequested.TrySetResult();
            }
        }, requestedResourceVersions);

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        // WatchAsync's idle timer is created before the retry-delay timer.
        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(1, listRequestCount);
        Assert.Equal(["1", "1"], requestedResourceVersions);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task WatchRetriesIndependentOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        var listRequestCount = 0;
        var requestedResourceVersions = new List<string?>();
        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects, () => Interlocked.Increment(ref listRequestCount));
        SetupPodWatchException(
            customObjects,
            new OperationCanceledException("The dependency canceled independently of the informer."),
            () =>
            {
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }
            },
            requestedResourceVersions);

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(2, watchRequestCount);
        Assert.Equal(1, listRequestCount);
        Assert.Equal(["1", "1"], requestedResourceVersions);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task HttpGoneDuringWatchTriggersRelist()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var secondListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;
        var requestedResourceVersions = new List<string?>();

        var customObjects = new Mock<ICustomObjectsOperations>();
        var listedPod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "relisted-pod",
                NamespaceProperty = "default",
                Uid = "pod-uid",
                Labels = new Dictionary<string, string> { ["app"] = "demo" },
            },
            Status = new V1PodStatus { Phase = "Running" },
        };
        var deletedPod = new TaskCompletionSource<V1Pod>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupPodListRequest(
            customObjects,
            () =>
            {
                if (Interlocked.Increment(ref listRequestCount) == 2)
                {
                    secondListRequested.TrySetResult();
                }
            },
            () => Volatile.Read(ref listRequestCount) == 1
                ? CreatePodListResponse([listedPod])
                : CreateEmptyPodListResponse(),
            requestedResourceVersions);
        SetupPodWatchException(customObjects, CreateHttpOperationException(HttpStatusCode.Gone));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        using var registration = informer.Register((eventType, pod) =>
        {
            if (eventType == WatchEventType.Deleted)
            {
                deletedPod.TrySetResult(pod);
            }
        });

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondListRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        var deleted = await deletedPod.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(listRequestCount >= 2, $"Expected a relist after HTTP 410, saw {listRequestCount} request(s).");
        Assert.Equal(["0", "1"], requestedResourceVersions);
        Assert.Same(listedPod, deleted);
        Assert.Equal("pod-uid", deleted.Metadata.Uid);
        Assert.Equal("demo", deleted.Metadata.Labels["app"]);
        Assert.Equal("Running", deleted.Status.Phase);
    }

    [Fact]
    public async Task RelistPublishesAddedModifiedAndDeletedReconciliationEvents()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var secondListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;
        var oldExisting = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "existing",
                NamespaceProperty = "default",
                ResourceVersion = "1",
                Labels = new Dictionary<string, string> { ["generation"] = "old" },
            },
            Status = new V1PodStatus { Phase = "Pending" },
        };
        var removed = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "removed",
                NamespaceProperty = "default",
                ResourceVersion = "1",
                Labels = new Dictionary<string, string> { ["retained"] = "last-known" },
            },
            Status = new V1PodStatus { Phase = "Running" },
        };
        var updatedExisting = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = "existing",
                NamespaceProperty = "default",
                ResourceVersion = "2",
                Labels = new Dictionary<string, string> { ["generation"] = "new" },
            },
            Status = new V1PodStatus { Phase = "Running" },
        };
        var added = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = "added", NamespaceProperty = "default", ResourceVersion = "2" },
        };
        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () =>
            {
                if (Interlocked.Increment(ref listRequestCount) == 2)
                {
                    secondListRequested.TrySetResult();
                }
            },
            responseFactory: null,
            responseFactoryAsync: () => Volatile.Read(ref listRequestCount) switch
            {
                1 => Task.FromResult(CreatePodListResponse([oldExisting, removed], "1")),
                2 => Task.FromResult(CreatePodListResponse([updatedExisting, added], "2")),
                _ => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(
                    new InvalidOperationException("Unexpected list request.")),
            });
        SetupPodWatchException(customObjects, CreateHttpOperationException(HttpStatusCode.Gone));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var received = new ConcurrentQueue<(WatchEventType EventType, V1Pod Resource)>();
        var allEventsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            received.Enqueue((eventType, pod));
            if (Interlocked.Increment(ref callbackCount) == 5)
            {
                allEventsReceived.TrySetResult();
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondListRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        await allEventsReceived.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        var events = received.ToArray();
        Assert.Equal(
            [
                (WatchEventType.Added, "existing"),
                (WatchEventType.Added, "removed"),
                (WatchEventType.Modified, "existing"),
                (WatchEventType.Added, "added"),
                (WatchEventType.Deleted, "removed"),
            ],
            events.Select(item => (item.EventType, item.Resource.Name())).ToArray());
        Assert.Equal("new", events[2].Resource.Metadata.Labels["generation"]);
        Assert.Same(removed, events[4].Resource);
        Assert.Equal("last-known", events[4].Resource.Metadata.Labels["retained"]);
        Assert.Equal("Running", events[4].Resource.Status.Phase);
        Assert.Equal(2, listRequestCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ExpiredListResourceVersionRetriesWithoutResourceVersion()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var thirdListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        var listRequestCount = 0;
        var requestedResourceVersions = new List<string?>();

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () =>
            {
                if (Interlocked.Increment(ref listRequestCount) == 3)
                {
                    thirdListRequested.TrySetResult();
                }
            },
            requestedResourceVersions: requestedResourceVersions,
            responseFactoryAsync: () => Volatile.Read(ref listRequestCount) switch
            {
                1 => Task.FromResult(CreatePodListResponse(Array.Empty<V1Pod>(), "1")),
                2 => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(CreateHttpOperationException(HttpStatusCode.Gone)),
                3 => Task.FromResult(CreatePodListResponse(Array.Empty<V1Pod>(), "2")),
                _ => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(new InvalidOperationException("Unexpected list request.")),
            });
        SetupPodWatchException(customObjects, CreateHttpOperationException(HttpStatusCode.Gone), () =>
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
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await timeProvider.WaitForTimerCountAsync(3, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(2);
        await thirdListRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(3, listRequestCount);
        Assert.Equal(["0", "1", null], requestedResourceVersions);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.Gone, "Gone")]
    [InlineData(0, "Expired")]
    public async Task ExpiredKubernetesStatusDuringListClearsResourceVersion(int statusCode, string reason)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var thirdListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;
        var requestedResourceVersions = new List<string?>();

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(
            customObjects,
            () =>
            {
                if (Interlocked.Increment(ref listRequestCount) == 3)
                {
                    thirdListRequested.TrySetResult();
                }
            },
            requestedResourceVersions: requestedResourceVersions,
            responseFactoryAsync: () => Volatile.Read(ref listRequestCount) switch
            {
                1 => Task.FromResult(CreatePodListResponse(Array.Empty<V1Pod>(), "1")),
                2 => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(new KubernetesException(new V1Status
                {
                    Code = statusCode,
                    Reason = reason,
                })),
                3 => Task.FromResult(CreatePodListResponse(Array.Empty<V1Pod>(), "2")),
                _ => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(new InvalidOperationException("Unexpected list request.")),
            });
        SetupPodWatchException(customObjects, CreateHttpOperationException(HttpStatusCode.Gone));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);
        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await timeProvider.WaitForTimerCountAsync(3, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(2);
        await thirdListRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(3, listRequestCount);
        Assert.Equal(["0", "1", null], requestedResourceVersions);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ForbiddenKubernetesStatusDuringListIsRetried()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var customObjects = new Mock<ICustomObjectsOperations>();
        var exception = new KubernetesException(new V1Status
        {
            Code = (int)HttpStatusCode.Forbidden,
            Reason = "Forbidden",
        });
        var listRequestCount = 0;
        SetupPodListRequest(
            customObjects,
            () => Interlocked.Increment(ref listRequestCount),
            responseFactoryAsync: () => Volatile.Read(ref listRequestCount) switch
            {
                1 => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(exception),
                2 => Task.FromResult(CreateEmptyPodListResponse()),
                _ => Task.FromException<HttpOperationResponse<KubernetesList<V1Pod>>>(new InvalidOperationException("Unexpected list request.")),
            });
        SetupPodWatchException(customObjects, new HttpRequestException("Stop test watch."));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(0);
        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(2, listRequestCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }

    [Fact]
    public async Task DisposingInformerCompletesActiveRegistrationsAndIgnoresLaterEvents()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var watchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects);
        SetupPodWatchException(customObjects, new HttpRequestException("Stop the test watch."), () => watchRequested.TrySetResult());

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var callbackCount = 0;
        var registration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => Interlocked.Increment(ref callbackCount)));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await watchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        informer.Dispose();
        var onEvent = typeof(ResourceInformer<V1Pod>).GetMethod("OnEvent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onEvent);
        onEvent.Invoke(informer, [WatchEventType.Added, new V1Pod { Metadata = new V1ObjectMeta { Name = "after-dispose" } }]);

        Assert.Equal(0, Volatile.Read(ref callbackCount));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        await registration.ReadyAsync(CancellationToken.None).DefaultTimeout();
        registration.Dispose();
    }

    [Fact]
    public async Task DisposedRegistrationDrainsQueuedEventsInOrderButReceivesNoLaterEvents()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedEventsDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<string>();
        var registration = informer.Register((ResourceInformerCallback<V1Pod>)((_, pod) =>
        {
            received.Enqueue(pod.Name());
            if (pod.Name() == "first")
            {
                callbackStarted.TrySetResult();
                releaseCallback.Task.GetAwaiter().GetResult();
            }

            if (pod.Name() == "third")
            {
                queuedEventsDelivered.TrySetResult();
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        DispatchWatchEvent(informer, WatchEventType.Added, "first", "1");
        await callbackStarted.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        DispatchWatchEvent(informer, WatchEventType.Modified, "second", "2");
        DispatchWatchEvent(informer, WatchEventType.Deleted, "third", "3");

        registration.Dispose();
        DispatchWatchEvent(informer, WatchEventType.Added, "after-dispose", "4");
        Assert.Equal(["first"], received.ToArray());

        releaseCallback.TrySetResult();
        await queuedEventsDelivered.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(["first", "second", "third"], received.ToArray());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task CallbackCanDisposeItselfAndRegisterAnotherCallbackReentrantly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        IResourceInformerRegistration? selfRegistration = null;
        IResourceInformerRegistration? nestedRegistration = null;
        var nestedInitialEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nestedModifiedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selfCallbackCount = 0;

        selfRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) =>
        {
            Interlocked.Increment(ref selfCallbackCount);
            selfRegistration!.Dispose();
            nestedRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            {
                if (eventType == WatchEventType.Added && pod.Name() == "reentrant")
                {
                    nestedInitialEvent.TrySetResult();
                }
                else if (eventType == WatchEventType.Modified && pod.Name() == "reentrant")
                {
                    nestedModifiedEvent.TrySetResult();
                }
            }));
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await selfRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        DispatchWatchEvent(informer, WatchEventType.Added, "reentrant", "1");
        await nestedInitialEvent.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        var nested = nestedRegistration ?? throw new InvalidOperationException("The callback did not register a nested receiver.");
        await nested.ReadyAsync(cancellation.Token).DefaultTimeout();
        DispatchWatchEvent(informer, WatchEventType.Modified, "reentrant", "2");
        await nestedModifiedEvent.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(1, Volatile.Read(ref selfCallbackCount));
        nested.Dispose();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ConcurrentRegistrationAndInformerDisposalAreLinearizable()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await clusterHost.StartAsync(cancellation.Token);

        var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();
        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();
        var baselineRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => { }));
        await baselineRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();

        const int registrationCount = 32;
        var startRace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = Enumerable.Range(0, registrationCount)
            .Select(_ => Task.Run(async () =>
            {
                await startRace.Task;
                try
                {
                    return informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => { }));
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            }))
            .ToArray();
        var disposal = Task.Run(async () =>
        {
            await startRace.Task;
            informer.Dispose();
        });

        startRace.TrySetResult();
        var registrationResults = await Task.WhenAll(registrations);
        await disposal;

        var successfulRegistrations = registrationResults.Where(registration => registration is not null).Cast<IResourceInformerRegistration>().ToArray();
        Assert.Equal(registrationCount, successfulRegistrations.Length + registrationResults.Count(registration => registration is null));
        var completedRegistrations = successfulRegistrations.Append(baselineRegistration).ToArray();
        await Task.WhenAll(completedRegistrations.Select(registration => registration.ReadyAsync(CancellationToken.None)));
        await Task.WhenAll(completedRegistrations.SelectMany(registration =>
        new Task[]
        {
            Task.Run(registration.Dispose),
            Task.Run(registration.Dispose),
        }));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        informer.Dispose();
    }

    [Fact]
    public async Task ConcurrentRegistrationAndRegistrationDisposalPreserveActiveReceivers()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.WatchResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();
        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        var activeReceiverCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activeRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((_, pod) =>
        {
            if (pod.Name() == "after-race")
            {
                activeReceiverCalled.TrySetResult();
            }
        }));
        await activeRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();

        const int registrationCount = 64;
        var startRace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedCallbackCount = 0;
        var registrationTasks = Enumerable.Range(0, registrationCount)
            .Select(_ => Task.Run(async () =>
            {
                await startRace.Task;
                var registration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => Interlocked.Increment(ref disposedCallbackCount)));
                registration.Dispose();
                await registration.ReadyAsync(CancellationToken.None);
            }))
            .ToArray();

        startRace.TrySetResult();
        await Task.WhenAll(registrationTasks);

        DispatchWatchEvent(informer, WatchEventType.Added, "after-race", "1");
        await activeReceiverCalled.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(0, Volatile.Read(ref disposedCallbackCount));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ClosedWatchStreamReconnectsWithoutRelisting()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var testHost = new HostBuilder()
            .ConfigureServices((_, services) =>
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
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var firstWatch = await clusterHost.Cluster.WaitForWatchRequestAsync(1).WaitAsync(cancellation.Token).DefaultTimeout();
        var secondWatch = await clusterHost.Cluster.WaitForWatchRequestAsync(2).WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(1, clusterHost.Cluster.ListRequests.Count);
        Assert.Equal(firstWatch.ResourceVersion, secondWatch.ResourceVersion);
        Assert.Equal("0", firstWatch.ResourceVersion);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
    }

    [Fact]
    public async Task RegistrationAfterInitialListReplaysCachedResourcesAsAdded()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var customObjects = new Mock<ICustomObjectsOperations>();
        var watchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listedPods = new[]
        {
            new V1Pod { Metadata = new V1ObjectMeta { Name = "first" } },
            new V1Pod { Metadata = new V1ObjectMeta { Name = "second" } },
        };
        SetupPodListRequest(customObjects, responseFactory: () => CreatePodListResponse(listedPods));
        SetupPodWatchException(customObjects, new HttpRequestException("End watch after initial list."), () => watchRequested.TrySetResult());

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        using var initialRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) => { }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();
        await initialRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await watchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        var received = new List<(WatchEventType EventType, string Name)>();
        using var registrationInProgress = new ThreadLocal<bool>();
        var callbackWasInline = 0;
        IResourceInformerRegistration lateRegistration;
        registrationInProgress.Value = true;
        try
        {
            lateRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            {
                if (registrationInProgress.Value)
                {
                    Interlocked.Exchange(ref callbackWasInline, 1);
                }

                received.Add((eventType, pod.Name()));
            }));
        }
        finally
        {
            registrationInProgress.Value = false;
        }

        using (lateRegistration)
        {
            await lateRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();

            Assert.Equal(0, Volatile.Read(ref callbackWasInline));
            Assert.Equal(
                [(WatchEventType.Added, "first"), (WatchEventType.Added, "second")],
                received);
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task LiveWatchEventQueuesBehindLateRegistrationCacheReplayDuringWatchEstablishment()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var releaseWatchResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources([new V1Pod
            {
                Metadata = new V1ObjectMeta { Name = "cached", NamespaceProperty = "default", ResourceVersion = "1" },
            }])
            .Build();
        clusterHost.Cluster.WatchResponseGate = releaseWatchResponse.Task;
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var liveEventDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var initialRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            if (eventType == WatchEventType.Modified && pod.Name() == "cached")
            {
                liveEventDispatched.TrySetResult();
            }
        }));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await initialRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WaitForWatchRequestAsync(1).WaitAsync(cancellation.Token).DefaultTimeout();

        var lateCacheCallbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLateCacheCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveCallbackReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<(WatchEventType EventType, string Name)>();
        using var lateRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            received.Enqueue((eventType, pod.Name()));
            if (eventType == WatchEventType.Added && pod.Name() == "cached")
            {
                lateCacheCallbackStarted.TrySetResult();
                releaseLateCacheCallback.Task.GetAwaiter().GetResult();
            }
            else if (eventType == WatchEventType.Modified && pod.Name() == "cached")
            {
                liveCallbackReceived.TrySetResult();
            }
        }));

        try
        {
            await lateCacheCallbackStarted.Task.WaitAsync(cancellation.Token).DefaultTimeout();
            clusterHost.Cluster.WatchEvents.Add(new WatchEvent
            {
                Type = "MODIFIED",
                Object = new ResourceObject
                {
                    ApiVersion = "v1",
                    Kind = "Pod",
                    Metadata = new V1ObjectMeta
                    {
                        Name = "cached",
                        NamespaceProperty = "default",
                        ResourceVersion = "2",
                    },
                },
            });
            releaseWatchResponse.TrySetResult();

            await liveEventDispatched.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        }
        finally
        {
            releaseLateCacheCallback.TrySetResult();
            releaseWatchResponse.TrySetResult();
        }

        await lateRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await liveCallbackReceived.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(
            [(WatchEventType.Added, "cached"), (WatchEventType.Modified, "cached")],
            received.ToArray());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RegistrationDuringInitialListDeliveryGetsOneAddedPerCachedResource()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var customObjects = new Mock<ICustomObjectsOperations>();
        var listedPods = new[]
        {
            new V1Pod { Metadata = new V1ObjectMeta { Name = "first" } },
            new V1Pod { Metadata = new V1ObjectMeta { Name = "second" } },
        };
        SetupPodListRequest(customObjects, responseFactory: () => CreatePodListResponse(listedPods));
        SetupPodWatchException(customObjects, new HttpRequestException("End watch after initial list."));

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        IResourceInformerRegistration? lateRegistration = null;
        var initialCallbackBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<(WatchEventType EventType, string Name)>();
        using var initialRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            if (lateRegistration is null)
            {
                lateRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((lateEventType, latePod) =>
                    received.Add((lateEventType, latePod.Name()))));
                initialCallbackBlocked.TrySetResult();
                releaseInitialCallback.Task.GetAwaiter().GetResult();
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        try
        {
            await initialCallbackBlocked.Task.WaitAsync(cancellation.Token).DefaultTimeout();
            var syncedLateRegistration = lateRegistration ?? throw new InvalidOperationException("The late registration was not created.");
            using var lateRegistrationLifetime = syncedLateRegistration;
            await syncedLateRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
            var informerReadiness = informer.ReadyAsync(cancellation.Token);
            var initialRegistrationReadiness = initialRegistration.ReadyAsync(cancellation.Token);
            Assert.False(informerReadiness.IsCompleted);
            Assert.False(initialRegistrationReadiness.IsCompleted);

            releaseInitialCallback.TrySetResult();
            await informerReadiness.DefaultTimeout();
            await initialRegistrationReadiness.DefaultTimeout();

            Assert.Equal(
                [(WatchEventType.Added, "first"), (WatchEventType.Added, "second")],
                received);
        }
        finally
        {
            releaseInitialCallback.TrySetResult();
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task ForbiddenHttpOperationExceptionDuringWatchIsRetried()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var listRequestCount = 0;
        var requestedWatchResourceVersions = new List<string?>();

        var customObjects = new Mock<ICustomObjectsOperations>();
        SetupPodListRequest(customObjects, () => Interlocked.Increment(ref listRequestCount));
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        SetupPodWatchException(
            customObjects,
            CreateHttpOperationException(HttpStatusCode.Forbidden),
            () =>
            {
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }
            },
            requestedWatchResourceVersions);

        var kubernetes = new Mock<IKubernetes>();
        kubernetes.SetupGet(client => client.CustomObjects).Returns(customObjects.Object);

        using var informer = new ResourceInformer<V1Pod>(
            kubernetes.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Equal(2, watchRequestCount);
        Assert.Equal(1, listRequestCount);
        Assert.Equal(["1", "1"], requestedWatchResourceVersions);
        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
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

    private sealed class CallbackFailureObserverLogger : ILogger<ResourceInformer<V1Pod>>
    {
        private readonly TaskCompletionSource _callbackFailureLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CallbackFailureLogged => _callbackFailureLogged.Task;

        public EventId CallbackFailureEventId { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is InvalidOperationException { Message: "Failure on a watch notification." })
            {
                CallbackFailureEventId = eventId;
                _callbackFailureLogged.TrySetResult();
            }
        }
    }

    private sealed class BlockingCallbackFailureLogger : ILogger<ResourceInformer<V1Pod>>
    {
        private readonly TaskCompletionSource _releaseLog = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LogEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LogCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error
                && exception is InvalidOperationException { Message: "Block while logging callback failure." })
            {
                LogEntered.TrySetResult();
                _releaseLog.Task.GetAwaiter().GetResult();
                LogCompleted.TrySetResult();
            }
        }

        public void ReleaseLog() => _releaseLog.TrySetResult();
    }
}
