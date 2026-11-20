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

        var informer = (ResourceInformer<V1Pod>)testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
        var pods = new Dictionary<NamespacedName, V1Pod>();
        var eventTypes = new List<WatchEventType>();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        informer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(informer.Status) && informer.Status == ResourceInformerStatus.Running)
            {
                running.TrySetResult();
            }
        };

        informer.StartWatching();
        using var registration = informer.Register((eventType, pod) =>
        {
            eventTypes.Add(eventType);
            pods[NamespacedName.From(pod)] = pod;
        });

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration.ReadyAsync(cancellation.Token);
        await running.Task.DefaultTimeout();

        Assert.Equal(shouldBe, pods.Keys);
        Assert.All(eventTypes, eventType => Assert.Equal(WatchEventType.Added, eventType));
    }

    [Fact]
    public async Task ResourcesWithApiGroupAreListed()
    {
        using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

        var (resources, shouldBe) = LoadTestResource<V1Deployment[], NamespacedName[]>(nameof(ResourcesWithApiGroupAreListed));

        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(resources)
            .Build();
        clusterHost.Cluster.WatchListSupported = true;
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
            var watchListRequest = await clusterHost.Cluster.WaitForWatchListRequestAsync(1)
                .WaitAsync(cancellation.Token).DefaultTimeout();
            Assert.Equal(ResourceInformerStatus.Running, informer.Status);
            Assert.True(watchListRequest.SendInitialEvents);
            Assert.Empty(clusterHost.Cluster.ListRequests);

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
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(pod)
            .Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
    public async Task ClosedWatchStreamTriggersReconnect()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var clusterHost = new TestClusterHostBuilder().Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.WaitForWatchRequestAsync(2).DefaultTimeout();

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task CancelledWatchReadDoesNotRaiseUnobservedTaskException()
    {
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobserved.Enqueue(e.Exception);
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var clusterHost = new TestClusterHostBuilder().Build();
            await clusterHost.StartAsync(cancellation.Token);

            var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FaultingWatchHandler(readGate)
            {
                InnerHandler = new HttpClientHandler(),
            };
            var configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
            using var client = new k8s.Kubernetes(configuration, handler);
            using var informer = new ResourceInformer<V1Pod>(
                client,
                Mock.Of<IHostApplicationLifetime>(),
                NullLogger<ResourceInformer<V1Pod>>.Instance);
            var firstEventReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            {
                if (eventType == WatchEventType.Modified && pod.Name() == "watched")
                {
                    firstEventReceived.TrySetResult();
                }
            }));
            var runTask = informer.RunAsync(cancellation.Token);
            informer.StartWatching();

            await handler.WatchRequested.Task.DefaultTimeout();
            await firstEventReceived.Task.DefaultTimeout();

            // The client is now blocked in its next stream read. Cancel first so WatcherExt's
            // cancellation continuation abandons the read, then fault that read from the server.
            cancellation.Cancel();
            try
            {
                await runTask.DefaultTimeout();
            }
            catch (OperationCanceledException)
            {
            }

            readGate.TrySetResult();

            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.DoesNotContain(unobserved, exception =>
                exception.ToString().Contains("FaultingWatchStream", StringComparison.Ordinal));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private sealed class FaultingWatchHandler(TaskCompletionSource readGate) : DelegatingHandler
    {
        public TaskCompletionSource WatchRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("watch=true", StringComparison.Ordinal)
                && !query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                WatchRequested.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FaultingWatchStream(readGate.Task)),
                });
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FaultingWatchStream(Task gate) : Stream
    {
        private static readonly byte[] s_firstEvent = System.Text.Encoding.UTF8.GetBytes(
            "{\"type\":\"MODIFIED\",\"object\":{\"apiVersion\":\"v1\",\"kind\":\"Pod\",\"metadata\":{\"name\":\"watched\",\"resourceVersion\":\"2\"}}}\n");
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == 0)
            {
                var length = Math.Min(s_firstEvent.Length, buffer.Length);
                s_firstEvent.AsMemory(0, length).CopyTo(buffer);
                _offset = length;
                return length;
            }

            await gate.WaitAsync(cancellationToken);
            throw new IOException("The request was aborted.");
        }

        // Do not complete a pending read on disposal: the test releases the gate afterwards.
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "1",
            Continue = "next",
            Items = [new ResourceObject { ApiVersion = "v1", Kind = "Pod", Metadata = new V1ObjectMeta { Name = "first" } }],
        });
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "2",
            Items = [new ResourceObject { ApiVersion = "v1", Kind = "Pod", Metadata = new V1ObjectMeta { Name = "second" } }],
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var resources = new List<string>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) => resources.Add(pod.Name())));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(CancellationToken.None).DefaultTimeout();
        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(["first", "second"], resources);
        var listRequests = clusterHost.Cluster.ListRequests.ToArray();
        Assert.Equal(2, listRequests.Length);
        Assert.Equal(new string?[] { null, "next" }, listRequests.Select(request => request.Continue));
        Assert.Equal(new string?[] { "0", null }, listRequests.Select(request => request.ResourceVersion));
        Assert.All(listRequests, request => Assert.Equal(1000, request.Limit));
    }

    [Fact]
    public async Task WatchListInitializesWithUnauthenticatedClient()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(new V1Pod { Metadata = new V1ObjectMeta { Name = "streamed" } })
            .Build();
        clusterHost.Cluster.WatchListSupported = true;
        await clusterHost.StartAsync(cancellation.Token);

        using var client = new k8s.Kubernetes(new KubernetesClientConfiguration
        {
            Host = clusterHost.Client.BaseUri.ToString(),
        });
        Assert.Null(client.Credentials);

        using var informer = new ResourceInformer<V1Pod>(
            client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var received = new ConcurrentQueue<string>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            if (eventType == WatchEventType.Added)
            {
                received.Enqueue(pod.Name());
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        try
        {
            await clusterHost.Cluster.WaitForWatchListRequestAsync(1).WaitAsync(cancellation.Token);
            await registration.ReadyAsync(cancellation.Token);
            Assert.Equal(["streamed"], received.ToArray());
            Assert.Empty(clusterHost.Cluster.ListRequests);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await runTask;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task WatchListInitializesCacheWithoutAnInitialListRequest()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(new V1Pod { Metadata = new V1ObjectMeta { Name = "streamed" } })
            .Build();
        clusterHost.Cluster.WatchListSupported = true;
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "MODIFIED",
            Resource = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "streamed", ResourceVersion = "2" },
            },
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            selector: new ResourceSelector<V1Pod>("metadata.name=streamed"));
        var received = new ConcurrentQueue<(WatchEventType EventType, string Name)>();
        var modifiedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
        {
            received.Enqueue((eventType, pod.Name()));
            if (eventType == WatchEventType.Modified)
            {
                modifiedEvent.TrySetResult();
            }
        }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var watchListRequest = await clusterHost.Cluster.WaitForWatchListRequestAsync(1)
            .WaitAsync(cancellation.Token).DefaultTimeout();
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        var firstWatchRequest = await clusterHost.Cluster.WaitForWatchRequestAsync(1)
            .WaitAsync(cancellation.Token).DefaultTimeout();
        await modifiedEvent.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        var secondWatchRequest = await clusterHost.Cluster.WaitForWatchRequestAsync(2)
            .WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.True(watchListRequest.Watch);
        Assert.True(watchListRequest.SendInitialEvents);
        Assert.True(watchListRequest.AllowWatchBookmarks);
        Assert.Equal("NotOlderThan", watchListRequest.ResourceVersionMatch);
        Assert.Equal(30, watchListRequest.TimeoutSeconds);
        Assert.Equal("metadata.name=streamed", watchListRequest.FieldSelector);
        Assert.Equal("1", firstWatchRequest.ResourceVersion);
        Assert.Equal("metadata.name=streamed", firstWatchRequest.FieldSelector);
        Assert.Equal("2", secondWatchRequest.ResourceVersion);
        Assert.Empty(clusterHost.Cluster.ListRequests);
        Assert.Equal(
            [(WatchEventType.Added, "streamed"), (WatchEventType.Modified, "streamed")],
            received.ToArray());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task WatchListDoesNotPublishInitialEventsUntilTheCompletionBookmarkArrives()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(new V1Pod { Metadata = new V1ObjectMeta { Name = "partial" } })
            .Build();
        clusterHost.Cluster.WatchListSupported = true;
        var releaseWatchListResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clusterHost.Cluster.WatchListResponseGate = releaseWatchListResponse.Task;
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var received = new ConcurrentQueue<(WatchEventType EventType, string Name)>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            received.Enqueue((eventType, pod.Name()))));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.WatchListInitialEventsSent.WaitAsync(cancellation.Token).DefaultTimeout();
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
        var eventsBeforeCompletionBookmark = received.ToArray();
        var readyBeforeCompletionBookmark = registration.ReadyAsync(cancellation.Token).IsCompleted;
        var statusBeforeCompletionBookmark = informer.Status;

        releaseWatchListResponse.TrySetResult();
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.Empty(eventsBeforeCompletionBookmark);
        Assert.False(readyBeforeCompletionBookmark);
        Assert.Equal(ResourceInformerStatus.Starting, statusBeforeCompletionBookmark);
        Assert.Equal([(WatchEventType.Added, "partial")], received.ToArray());
    }

    [Fact]
    public async Task WatchListBootstrapTimeoutFallsBackToListWithoutPublishingPartialEvents()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(new V1Pod { Metadata = new V1ObjectMeta { Name = "partial" } })
            .Build();
        clusterHost.Cluster.WatchListSupported = true;
        var releaseWatchListResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clusterHost.Cluster.WatchListResponseGate = releaseWatchListResponse.Task;
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "2",
            Continue = null,
            Items =
            [
                new ResourceObject
                {
                    ApiVersion = "v1",
                    Kind = "Pod",
                    Metadata = new V1ObjectMeta { Name = "listed", ResourceVersion = "2" },
                },
            ],
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeoutSeconds: 1);
        var received = new ConcurrentQueue<(WatchEventType EventType, string Name)>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            received.Enqueue((eventType, pod.Name()))));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await clusterHost.Cluster.WatchListInitialEventsSent.WaitAsync(cancellation.Token).DefaultTimeout();
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();
        releaseWatchListResponse.TrySetResult();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Assert.Single(clusterHost.Cluster.ListRequests);
        Assert.Equal(1, clusterHost.Cluster.WatchListRequests.Single().TimeoutSeconds);
        Assert.Equal([(WatchEventType.Added, "listed")], received.ToArray());
    }

    [Fact]
    public async Task RejectedWatchListFallsBackToList()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(new V1Pod { Metadata = new V1ObjectMeta { Name = "listed" } })
            .Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var received = new List<(WatchEventType EventType, string Name)>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            received.Add((eventType, pod.Name()))));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var watchListRequest = await clusterHost.Cluster.WaitForWatchListRequestAsync(1)
            .WaitAsync(cancellation.Token).DefaultTimeout();
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        Assert.True(watchListRequest.SendInitialEvents);
        Assert.Single(clusterHost.Cluster.ListRequests);
        Assert.Equal([(WatchEventType.Added, "listed")], received);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task WatchListEndingBeforeInitialEventsEndFallsBackWithoutPublishingPartialState()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(new V1Pod { Metadata = new V1ObjectMeta { Name = "partial" } })
            .Build();
        clusterHost.Cluster.WatchListSupported = true;
        clusterHost.Cluster.WatchListEndsBeforeInitialEventsEnd = true;
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "2",
            Continue = null,
            Items =
            [
                new ResourceObject
                {
                    ApiVersion = "v1",
                    Kind = "Pod",
                    Metadata = new V1ObjectMeta { Name = "listed", ResourceVersion = "2" },
                },
            ],
        });
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var received = new List<(WatchEventType EventType, string Name)>();
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            received.Add((eventType, pod.Name()))));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var watchListRequest = await clusterHost.Cluster.WaitForWatchListRequestAsync(1)
            .WaitAsync(cancellation.Token).DefaultTimeout();
        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        Assert.True(watchListRequest.SendInitialEvents);
        Assert.Single(clusterHost.Cluster.ListRequests);
        Assert.Equal([(WatchEventType.Added, "listed")], received);

        var cachedResources = new ConcurrentQueue<(WatchEventType EventType, string Name)>();
        using var cacheVerifier = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) =>
            cachedResources.Enqueue((eventType, pod.Name()))));
        await cacheVerifier.ReadyAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal([(WatchEventType.Added, "listed")], cachedResources.ToArray());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
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
            Continue = null,
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
        var listRequestCount = 0;
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "1",
            Continue = "next",
            Items = [new ResourceObject { ApiVersion = "v1", Kind = "Pod", Metadata = new V1ObjectMeta { Name = "partial" } }],
        });
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "2",
            Items = [new ResourceObject { ApiVersion = "v1", Kind = "Pod", Metadata = new V1ObjectMeta { Name = "recovered" } }],
        });
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && Interlocked.Increment(ref listRequestCount) == 2)
            {
                throw new InvalidOperationException("The second list page failed.");
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        var listRequests = clusterHost.Cluster.ListRequests.ToArray();
        Assert.All(listRequests, request => Assert.Equal(1, request.Limit));
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
        var requestedResourceVersions = new List<string?>();
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

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "1",
            Continue = "initial-next",
            Items = [ResourceSerializers.Convert<ResourceObject>(existing)],
        });
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "1",
            Items = [ResourceSerializers.Convert<ResourceObject>(removed)],
        });
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "2",
            Continue = "relist-next",
            Items = [ResourceSerializers.Convert<ResourceObject>(updatedExisting)],
        });
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "2",
            Continue = "relist-next",
            Items = [ResourceSerializers.Convert<ResourceObject>(updatedExisting)],
        });
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "2",
            Items = [ResourceSerializers.Convert<ResourceObject>(added)],
        });
        var firstWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            if (query.Contains("watch=true", StringComparison.Ordinal))
            {
                firstWatchRequested.TrySetResult();
                throw CreateHttpOperationException(HttpStatusCode.Gone);
            }

            requestedResourceVersions.Add(query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
            if (Interlocked.Increment(ref listRequestCount) == 4)
            {
                relistPageFailed.TrySetResult();
                throw new InvalidOperationException("The second relist page failed.");
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        Assert.Equal("removed", events[4].Resource.Metadata.Name);
        Assert.Equal(6, listRequestCount);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task NamespacedInformerUsesNamespacedListEndpoint()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            @namespace: "test-namespace");
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) => { }));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(CancellationToken.None).DefaultTimeout();
        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotEmpty(clusterHost.Cluster.ListRequests);
        Assert.All(clusterHost.Cluster.ListRequests, request => Assert.Equal("test-namespace", request.Namespace));
    }
    [Fact]
    public async Task DisposedClientDuringWatchReconnectCompletesCleanly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        using var registration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) => { }));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await registration.ReadyAsync(CancellationToken.None).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.DefaultTimeout();

        clusterHost.Client.Dispose();
        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
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
            Resource = new ResourceObject
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
            Resource = new ResourceObject
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
            Resource = new ResourceObject
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
            Resource = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "pod", ResourceVersion = "1" },
            },
        });
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "MODIFIED",
            Resource = new ResourceObject
            {
                ApiVersion = "v1",
                Kind = "Pod",
                Metadata = new V1ObjectMeta { Name = "pod", ResourceVersion = "2" },
            },
        });
        clusterHost.Cluster.WatchEvents.Add(new WatchEvent
        {
            Type = "DELETED",
            Resource = new ResourceObject
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
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

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
            Resource = new ResourceObject
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
        using var registration = informer.Register((WatchEventType eventType, V1Pod pod) => throw new InvalidOperationException("boom"));
        var healthyCallbackCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var healthyRegistration = informer.Register((WatchEventType eventType, V1Pod pod) => healthyCallbackCalled.TrySetResult());

        await clusterHost.StartAsync(cancellation.Token);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await healthyRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await healthyCallbackCalled.Task.DefaultTimeout();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => registration.ReadyAsync(cancellation.Token));
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

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                watchRequested.TrySetResult();
                throw new HttpRequestException(
                    "Error while copying content to a stream.",
                    new EndOfStreamException("Attempted to read past the end of the stream."));
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var informer = new ResourceInformer<V1Pod>(clusterHost.Client, Mock.Of<IHostApplicationLifetime>(), loggerFactory.CreateLogger<ResourceInformer<V1Pod>>());
        var runTask = informer.RunAsync(cancellation.Token);

        informer.StartWatching();

        await watchRequested.Task.DefaultTimeout();

        cancellation.Cancel();

        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

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

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                throw new KubernetesException(new V1Status { Code = statusCode, Reason = reason });
            }

            if (Interlocked.Increment(ref listRequestCount) == 2)
            {
                secondListRequested.TrySetResult();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var loggerFactory = LoggerFactory.Create(builder => { });
        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            loggerFactory.CreateLogger<ResourceInformer<V1Pod>>(),
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);

        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondListRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        cancellation.Cancel();

        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(listRequestCount >= 2, $"Expected at least 2 list requests, saw {listRequestCount}.");
    }

    [Theory]
    [InlineData("2")]
    [InlineData("Thu, 01 Jan 1970 00:00:02 GMT")]
    public async Task ListRetryHonorsRetryAfterHeader(string retryAfterValue)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var listRequestCount = 0;

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && Interlocked.Increment(ref listRequestCount) == 1)
            {
                var failure = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                failure.Headers.TryAddWithoutValidation("Retry-After", retryAfterValue);
                return Task.FromResult<HttpResponseMessage?>(failure);
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        timeProvider.VerifyTimer(0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, listRequestCount);
        timeProvider.FireTimer(0);

        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(2, listRequestCount);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
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

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                requestedResourceVersions.Add(request.RequestUri.Query
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }

                var failure = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                failure.Headers.TryAddWithoutValidation("Retry-After", retryAfterValue);
                return Task.FromResult<HttpResponseMessage?>(failure);
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        Assert.Equal(["0", "0"], requestedResourceVersions);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task ListRetryBackoffIncreasesAcrossConsecutiveFailures()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var listRequestCount = 0;
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && Interlocked.Increment(ref listRequestCount) <= 3)
            {
                return Task.FromResult<HttpResponseMessage?>(
                    new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(4, listRequestCount);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
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
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                var attempt = Interlocked.Increment(ref watchRequestCount);
                if (attempt <= watchRequests.Length)
                {
                    watchRequests[attempt - 1].TrySetResult();
                }

                throw new HttpRequestException("The watch failed quickly.");
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task WatchRetryBackoffResetsAfterSustainedWatch()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var watchRequests = Enumerable.Range(0, 5)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var sustainedWatchFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = async (request, token) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                var attempt = Interlocked.Increment(ref watchRequestCount);
                if (attempt <= watchRequests.Length)
                {
                    watchRequests[attempt - 1].TrySetResult();
                }

                if (attempt == watchRequests.Length)
                {
                    await sustainedWatchFailure.Task.WaitAsync(token);
                    throw new HttpRequestException("The sustained watch failed.");
                }

                throw new HttpRequestException("The watch failed quickly.");
            }

            return null;
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
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
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = async (request, token) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                listRequestStarted.TrySetResult();
                await releaseListRequest.Task.WaitAsync(token);
            }

            return null;
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
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
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && Interlocked.Increment(ref listRequestCount) == 1)
            {
                throw new KubernetesException(new V1Status
                {
                    Code = (int)HttpStatusCode.TooManyRequests,
                    Reason = "TooManyRequests",
                    Details = new V1StatusDetails { RetryAfterSeconds = retryAfterSeconds },
                });
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();
        Assert.Equal(2, listRequestCount);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
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
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && Interlocked.Increment(ref listRequestCount) == 1)
            {
                var failure = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                failure.Headers.TryAddWithoutValidation("Retry-After", retryAfterValue);
                return Task.FromResult<HttpResponseMessage?>(failure);
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HttpOperationExceptionDuringListRetriesForTransientStatus(HttpStatusCode statusCode)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var listRequestCount = 0;
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && Interlocked.Increment(ref listRequestCount) == 1)
            {
                return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(statusCode));
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(1, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(0);
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

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
        var requestedWatchResourceVersions = new List<string?>();

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                requestedWatchResourceVersions.Add(request.RequestUri.Query
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }

                return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(statusCode));
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();
        cancellation.Cancel();

        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(ResourceInformerStatus.Stopped, informer.Status);
        Assert.Single(clusterHost.Cluster.ListRequests);
        Assert.Equal(["0", "0"], requestedWatchResourceVersions);
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
        var requestedResourceVersions = new List<string?>();
        Exception watchException = useTaskCanceledException
            ? new TaskCanceledException("The watch request timed out independently.")
            : new IOException("The socket was reset.", new SocketException((int)SocketError.ConnectionReset));

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                requestedResourceVersions.Add(request.RequestUri.Query
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }

                throw watchException;
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        // WatchAsync's idle timer is created before the retry-delay timer.
        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Single(clusterHost.Cluster.ListRequests);
        Assert.Equal(["0", "0"], requestedResourceVersions);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task WatchRetriesIndependentOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        var requestedResourceVersions = new List<string?>();
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                requestedResourceVersions.Add(request.RequestUri.Query
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }

                throw new OperationCanceledException("The dependency canceled independently of the informer.");
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondWatchRequested.Task.WaitAsync(cancellation.Token).DefaultTimeout();

        Assert.Equal(2, watchRequestCount);
        Assert.Single(clusterHost.Cluster.ListRequests);
        Assert.Equal(["0", "0"], requestedResourceVersions);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task HttpGoneDuringWatchTriggersRelist()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var secondListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;
        var requestedResourceVersions = new List<string?>();

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
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "1",
            Items = [ResourceSerializers.Convert<ResourceObject>(listedPod)],
        });
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"type\":\"ERROR\",\"object\":{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"status\":\"Failure\",\"reason\":\"Gone\",\"code\":410}}\n"),
                });
            }

            if (request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            requestedResourceVersions.Add(request.RequestUri.Query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
            if (Interlocked.Increment(ref listRequestCount) == 2)
            {
                secondListRequested.TrySetResult();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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

        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(listRequestCount >= 2, $"Expected a relist after HTTP 410, saw {listRequestCount} request(s).");
        Assert.Equal(["0", "1"], requestedResourceVersions);
        Assert.Equal("relisted-pod", deleted.Metadata.Name);
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
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "1",
            Items = [ResourceSerializers.Convert<ResourceObject>(oldExisting), ResourceSerializers.Convert<ResourceObject>(removed)],
        });
        clusterHost.Cluster.EnqueueListResponse(new ListResult
        {
            ResourceVersion = "2",
            Items = [ResourceSerializers.Convert<ResourceObject>(updatedExisting), ResourceSerializers.Convert<ResourceObject>(added)],
        });
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"type\":\"ERROR\",\"object\":{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"status\":\"Failure\",\"reason\":\"Gone\",\"code\":410}}\n"),
                });
            }

            if (request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            if (Interlocked.Increment(ref listRequestCount) == 2)
            {
                secondListRequested.TrySetResult();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
        Assert.Equal("removed", events[4].Resource.Metadata.Name);
        Assert.Equal("last-known", events[4].Resource.Metadata.Labels["retained"]);
        Assert.Equal("Running", events[4].Resource.Status.Phase);
        Assert.Equal(2, listRequestCount);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task ExpiredListResourceVersionRetriesWithoutResourceVersion()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new TestTimeProvider();
        var thirdListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchRequestCount = 0;
        var listRequestCount = 0;
        var requestedResourceVersions = new List<string?>();

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.EnqueueListResponse(new ListResult { ResourceVersion = "1" });
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            if (query.Contains("watch=true", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref watchRequestCount) == 2)
                {
                    secondWatchRequested.TrySetResult();
                }

                firstWatchRequested.TrySetResult();
                throw CreateHttpOperationException(HttpStatusCode.Gone);
            }

            requestedResourceVersions.Add(query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
            var listAttempt = Interlocked.Increment(ref listRequestCount);
            if (listAttempt == 2)
            {
                secondListRequested.TrySetResult();
            }

            if (listAttempt == 3)
            {
                thirdListRequested.TrySetResult();
            }

            if (listAttempt == 2)
            {
                throw CreateHttpOperationException(HttpStatusCode.Gone);
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await firstWatchRequested.Task.DefaultTimeout();
        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        timeProvider.FireTimer(1);
        await secondListRequested.Task.DefaultTimeout();
        timeProvider.FireTimer(2);
        await thirdListRequested.Task.DefaultTimeout();
        await secondWatchRequested.Task.DefaultTimeout();
        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }

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
        var secondListRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWatchRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listRequestCount = 0;
        var requestedResourceVersions = new List<string?>();

        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.EnqueueListResponse(new ListResult { ResourceVersion = "1" });
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            var query = request.RequestUri!.Query;
            if (query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            if (query.Contains("watch=true", StringComparison.Ordinal))
            {
                firstWatchRequested.TrySetResult();
                throw CreateHttpOperationException(HttpStatusCode.Gone);
            }

            requestedResourceVersions.Add(query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
            var attempt = Interlocked.Increment(ref listRequestCount);
            if (attempt == 2)
            {
                secondListRequested.TrySetResult();
                throw new KubernetesException(new V1Status { Code = statusCode, Reason = reason });
            }

            if (attempt == 3)
            {
                thirdListRequested.TrySetResult();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance,
            timeProvider: timeProvider);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await firstWatchRequested.Task.DefaultTimeout();
        await timeProvider.WaitForTimerCountAsync(2, cancellation.Token).DefaultTimeout();
        for (var timer = 0; timer < timeProvider.TimerCount && !secondListRequested.Task.IsCompleted; timer++)
        {
            timeProvider.FireTimer(timer);
        }

        await secondListRequested.Task.DefaultTimeout();
        for (var timer = 0; timer < timeProvider.TimerCount && !thirdListRequested.Task.IsCompleted; timer++)
        {
            timeProvider.FireTimer(timer);
        }

        await thirdListRequested.Task.DefaultTimeout();

        Assert.Equal(3, listRequestCount);
        Assert.Equal(["0", "1", null], requestedResourceVersions);

        cancellation.Cancel();
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task ForbiddenKubernetesStatusDuringListFaultsInformer()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exception = new KubernetesException(new V1Status
        {
            Code = (int)HttpStatusCode.Forbidden,
            Reason = "Forbidden",
        });
        var listRequestCount = 0;
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && Interlocked.Increment(ref listRequestCount) == 1)
            {
                throw exception;
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var thrown = await Assert.ThrowsAsync<KubernetesException>(
            () => runTask.WaitAsync(cancellation.Token).DefaultTimeout());

        Assert.Equal(exception.Status.Code, thrown.Status.Code);
        Assert.Equal(exception.Status.Reason, thrown.Status.Reason);
        Assert.Equal(1, listRequestCount);
        Assert.Equal(ResourceInformerStatus.Faulted, informer.Status);
    }

    [Fact]
    public async Task DisposingInformerCompletesActiveRegistrationsAndIgnoresLaterEvents()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clusterHost = new TestClusterHostBuilder().Build();
        await clusterHost.StartAsync(cancellation.Token);

        var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        var callbackCount = 0;
        var registration = informer.Register((ResourceInformerCallback<V1Pod>)((_, _) => Interlocked.Increment(ref callbackCount)));
        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        await informer.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();
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
        }, TestContext.Current.CancellationToken);

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

        Assert.Single(clusterHost.Cluster.ListRequests);
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
        var listedPods = new[]
        {
            new V1Pod { Metadata = new V1ObjectMeta { Name = "first" } },
            new V1Pod { Metadata = new V1ObjectMeta { Name = "second" } },
        };
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(listedPods)
            .Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);
        using var initialRegistration = informer.Register((ResourceInformerCallback<V1Pod>)((eventType, pod) => { }));

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();
        await initialRegistration.ReadyAsync(cancellation.Token).DefaultTimeout();
        await clusterHost.Cluster.WatchRequested.WaitAsync(cancellation.Token).DefaultTimeout();

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
                Resource = new ResourceObject
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
        try
        {
            await runTask.DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task RegistrationDuringInitialListDeliveryGetsOneAddedPerCachedResource()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listedPods = new[]
        {
            new V1Pod { Metadata = new V1ObjectMeta { Name = "first" } },
            new V1Pod { Metadata = new V1ObjectMeta { Name = "second" } },
        };
        using var clusterHost = new TestClusterHostBuilder()
            .UseInitialResources(listedPods)
            .Build();
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
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
    public async Task ForbiddenHttpOperationExceptionDuringWatchFaultsInformer()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestedWatchResourceVersions = new List<string?>();

        var watchRequestCount = 0;
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref watchRequestCount);
                requestedWatchResourceVersions.Add(request.RequestUri.Query
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .FirstOrDefault(part => part[0] == "resourceVersion")?.ElementAtOrDefault(1));
                return Task.FromResult<HttpResponseMessage?>(
                    new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        var runTask = informer.RunAsync(cancellation.Token);
        informer.StartWatching();

        var thrown = await Assert.ThrowsAsync<HttpOperationException>(
            () => runTask.WaitAsync(cancellation.Token).DefaultTimeout());
        Assert.Equal(HttpStatusCode.Forbidden, thrown.Response?.StatusCode);
        Assert.Equal(1, watchRequestCount);
        Assert.Single(clusterHost.Cluster.ListRequests);
        Assert.Equal(["0"], requestedWatchResourceVersions);
        Assert.Equal(ResourceInformerStatus.Faulted, informer.Status);
    }

    [Fact]
    public async Task RunInfiniteFaultsForNonRetryableKubernetesStatus()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = new KubernetesException(new V1Status
        {
            Code = (int)HttpStatusCode.Forbidden,
            Reason = "Forbidden",
        });
        var listRequestCount = 0;
        using var clusterHost = new TestClusterHostBuilder().Build();
        clusterHost.Cluster.RequestHandler = (request, _) =>
        {
            if (!request.RequestUri!.Query.Contains("watch=true", StringComparison.Ordinal)
                && !request.RequestUri.Query.Contains("sendInitialEvents=true", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref listRequestCount);
                throw error;
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await clusterHost.StartAsync(cancellation.Token);

        using var informer = new ResourceInformer<V1Pod>(
            clusterHost.Client,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<ResourceInformer<V1Pod>>.Instance);

        var thrown = await Assert.ThrowsAsync<KubernetesException>(
            () => informer.RunInfinite(cancellation.Token).WaitAsync(cancellation.Token).DefaultTimeout());

        Assert.Equal(error.Status.Code, thrown.Status.Code);
        Assert.Equal(ResourceInformerStatus.Faulted, informer.Status);
        Assert.Equal(1, listRequestCount);
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
