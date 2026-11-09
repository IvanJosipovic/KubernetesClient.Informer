# KubernetesClient.Informer

[![NuGet](https://img.shields.io/nuget/vpre/KubernetesClient.Informer.svg?style=flat-square)](https://www.nuget.org/packages/KubernetesClient.Informer)
[![NuGet downloads](https://img.shields.io/nuget/dt/KubernetesClient.Informer.svg?style=flat-square)](https://www.nuget.org/packages/KubernetesClient.Informer)
[![codecov](https://codecov.io/gh/IvanJosipovic/KubernetesClient.Informer/graph/badge.svg?token=zXQwCiXAYf)](https://codecov.io/gh/IvanJosipovic/KubernetesClient.Informer)

`KubernetesClient.Informer` is a typed list-and-watch client for Kubernetes resources. It performs an initial list, then watches the Kubernetes API for changes and notifies your application through callbacks. It is based on the informer approach used by [YARP's Kubernetes controller](https://github.com/dotnet/yarp/tree/main/src/Kubernetes.Controller).

Use it to keep application state synchronized with resources such as Pods, Deployments, or custom resources without repeatedly polling the API.

## Features

- Typed notifications for added, modified, and deleted resources.
- Initial listing with pagination, followed by a resource-version-based watch.
- Reconnect and relist handling for transient errors and expired resource versions.
- Optional Kubernetes field selectors.
- Hosted-service integration and lifecycle status reporting.
- OpenTelemetry activities for informer, list, watch, and event operations.

## Install

```sh
dotnet add package KubernetesClient.Informer
```

The library targets .NET 10 and .NET 11 RC3. `AddKubernetesCore()` uses the default Kubernetes client configuration, which reads the local kubeconfig when available or in-cluster configuration when running in Kubernetes. You can also register your own `IKubernetes` client before calling `AddKubernetesCore()`.

## Quick start

An executable version of this example is available in [`samples/PodWatcher`](samples/PodWatcher).

This console app subscribes to Pod changes, starts the informer, waits for the initial list to complete, and then runs until shutdown (for example, Ctrl+C):

```csharp
using System;
using k8s.Models;
using KubernetesClient.Informer.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddKubernetesCore();
builder.Services.RegisterResourceInformer<V1Pod>();

using var host = builder.Build();

var pods = host.Services.GetRequiredService<IResourceInformer<V1Pod>>();
using var registration = pods.Register((eventType, pod) =>
{
    var name = pod.Metadata?.Name ?? "<unnamed>";
    var ns = pod.Metadata?.NamespaceProperty ?? "default";
    Console.WriteLine($"{eventType}: {ns}/{name}");
});

// Register callbacks before starting the host so no events are missed.
await host.StartAsync();

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
await registration.ReadyAsync(lifetime.ApplicationStopping);
Console.WriteLine("Initial Pod list is ready. Watching for changes...");

await host.WaitForShutdownAsync();
```

The initial list is delivered through the same callback: listed resources arrive as `Added` events. `ReadyAsync` completes after the initial list has been processed; later changes continue to arrive through the watch. Dispose the returned registration when you no longer need notifications.

`RegisterResourceInformer` starts watching automatically when the host starts. For coordinated startup, pass `startWatchingOnHostStart: false` and call `StartWatching()` when ready.

### Filter with a field selector

To ask Kubernetes to return only matching resources, register the informer with a field selector:

```csharp
builder.Services.RegisterResourceInformer<V1Pod, ResourceInformer<V1Pod>>(
    "status.phase=Running");
```

Use a field selector supported by the Kubernetes resource's list endpoint.

## Lifecycle status

`IResourceInformer.Status` reports the current state: `NotStarted`, `Starting`, `Running`, `Retrying`, `Stopped`, or `Faulted`. The concrete `ResourceInformer<TResource>` also implements `INotifyPropertyChanged` and raises `PropertyChanged` when `Status` changes.

## OpenTelemetry tracing

Informers emit activities from the `KubernetesClient.Informer` activity source. If your application uses OpenTelemetry, add this source to its tracing provider:

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(InformerDiagnostics.ActivitySourceName));
```

Activities cover informer lifecycle, Kubernetes resource listing, watch connections, and individual watch events. They include resource group, version, kind, namespace, and event details where available.
