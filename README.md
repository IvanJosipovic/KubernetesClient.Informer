# KubernetesClient.Informer

[![Nuget](https://img.shields.io/nuget/vpre/KubernetesClient.Informer.svg?style=flat-square)](https://www.nuget.org/packages/KubernetesClient.Informer)
[![Nuget)](https://img.shields.io/nuget/dt/KubernetesClient.Informer.svg?style=flat-square)](https://www.nuget.org/packages/KubernetesClient.Informer)
[![codecov](https://codecov.io/gh/IvanJosipovic/KubernetesClient.Informer/graph/badge.svg?token=zXQwCiXAYf)](https://codecov.io/gh/IvanJosipovic/KubernetesClient.Informer)


A Kubernetes Resource Informer implementation based on [Yarp.Kubernetes.Controller](https://github.com/dotnet/yarp/tree/main/src/Kubernetes.Controller)

## OpenTelemetry tracing

Informers emit OpenTelemetry activities from the `KubernetesClient.Informer` activity source. Register that source with the application's OpenTelemetry tracing provider:

```csharp
services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(InformerDiagnostics.ActivitySourceName));
```

The source includes spans for the informer lifecycle, Kubernetes resource listing, watch connections, and individual watch events. Spans include resource group, version, kind, namespace, and event details where available.
