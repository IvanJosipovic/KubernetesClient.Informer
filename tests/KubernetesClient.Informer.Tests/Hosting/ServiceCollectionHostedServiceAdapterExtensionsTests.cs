// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s.Models;
using KubernetesClient.Informer.Client;
using KubernetesClient.Informer.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace KubernetesClient.Informer.Hosting.Tests;

public class ServiceCollectionHostedServiceAdapterExtensionsTests
{
    [Fact]
    public async Task HostedServiceAdapterStartsAnInformerByDefault()
    {
        var informer = new Mock<IResourceInformer<V1Pod>>();
        informer.Setup(service => service.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var adapter = new HostedServiceAdapter<IResourceInformer<V1Pod>>(informer.Object);

        await adapter.StartAsync(TestContext.Current.CancellationToken);

        informer.Verify(service => service.StartWatching(), Times.Once);
    }

    [Fact]
    public async Task RegisterHostedServiceStartsAnInformerByDefault()
    {
        var informer = new Mock<IResourceInformer<V1Pod>>();
        informer.Setup(service => service.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        informer.Setup(service => service.StopAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        using var host = new HostBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<IResourceInformer<V1Pod>>(informer.Object);
                services.RegisterHostedService<IResourceInformer<V1Pod>>();
            })
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        informer.Verify(service => service.StartWatching(), Times.Once);
    }
}
