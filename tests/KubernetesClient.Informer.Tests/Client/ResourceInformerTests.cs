// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using Xunit;
using KubernetesClient.Informer.Common;
using KubernetesClient.Informer.Client;
using KubernetesClient.Informer.Tests.TestCluster;
using KubernetesClient.Informer.Tests.Utils;

namespace KubernetesClient.Informer.Client.Tests;

public class ResourceInformerTests
{
    private static (TResources Resources, TShouldBe ShouldBe) LoadTestResource<TResources, TShouldBe>(string name)
    {
        var resourcesYaml = File.ReadAllText(Path.Combine("testassets/resource-informer", name, "resources.yaml"));
        var shouldBeYaml = File.ReadAllText(Path.Combine("testassets/resource-informer", name, "shouldbe.yaml"));

        var resources = ResourceSerializers.DeserializeYaml<TResources>(resourcesYaml);
        var shouldBe = ResourceSerializers.DeserializeYaml<TShouldBe>(shouldBeYaml);

        return (resources, shouldBe);
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

        informer.StartWatching();
        using var registration = informer.Register((eventType, pod) =>
        {
            pods[NamespacedName.From(pod)] = pod;
        });

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration.ReadyAsync(cancellation.Token);

        Assert.Equal(shouldBe, pods.Keys);
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

        var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Deployment>>();
        var deployments = new Dictionary<NamespacedName, V1Deployment>();

        informer.StartWatching();
        using var registration = informer.Register((eventType, deployment) =>
        {
            deployments[NamespacedName.From(deployment)] = deployment;
        });

        await clusterHost.StartAsync(cancellation.Token);
        await testHost.StartAsync(cancellation.Token);

        await registration.ReadyAsync(cancellation.Token);

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

        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellation.Token);
        Assert.Null(clusterHost.Cluster.LastListParameters);

        informer.StartWatching();

        await registration.ReadyAsync(cancellation.Token).DefaultTimeout();

        Assert.NotNull(clusterHost.Cluster.LastListParameters);
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

    //[Fact]
    //public async Task ConfiguredRequestOptionsArePassedToListAndWatchCalls()
    //{
    //    using var cancellation = new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));

    //    var (resources, _) = LoadTestResource<V1Pod[], NamespacedName[]>(nameof(ResourcesAreListedWhenReadyAsyncIsComplete));

    //    using var clusterHost = new TestClusterHostBuilder()
    //        .UseInitialResources(resources)
    //        .Build();

    //    using var testHost = new HostBuilder()
    //        .ConfigureServices((context, services) =>
    //        {
    //            services.RegisterResourceInformer<V1Pod, ResourceInformer<V1Pod>>(fieldSelector: null);

    //            services.Configure<KubernetesClientOptions>(options =>
    //            {
    //                options.Configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(clusterHost.KubeConfig);
    //            });

    //            services.AddKubernetesCore();
    //        })
    //        .Build();

    //    var informer = testHost.Services.GetRequiredService<IResourceInformer<V1Pod>>();
    //    using var registration = informer.Register((eventType, pod) => { });

    //    informer.StartWatching();

    //    await clusterHost.StartAsync(cancellation.Token);
    //    await testHost.StartAsync(cancellation.Token);

    //    var listRequest = await clusterHost.Cluster.ListRequested.DefaultTimeout();
    //    var watchRequest = await clusterHost.Cluster.WatchRequested.DefaultTimeout();

    //    Assert.Equal(33, listRequest.TimeoutSeconds);
    //    Assert.True(watchRequest.Watch);
    //    Assert.Equal(33, watchRequest.TimeoutSeconds);
    //}

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
}
