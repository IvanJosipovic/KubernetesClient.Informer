using k8s;
using k8s.Models;
using KubernetesClient.Informer.Client;
using System;

var logPodEvent = LoggerMessage.Define<WatchEventType, string?, string?>(
    LogLevel.Information,
    new EventId(0, "PodEvent"),
    "{EventType}: {Namespace}/{PodName}");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddKubernetesCore();
builder.Services.RegisterResourceInformer<V1Pod>();

var host = builder.Build();

var podInformer = host.Services.GetRequiredService<IResourceInformer<V1Pod>>();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PodWatcher");

using var registration = podInformer.Register((eventType, pod) =>
{
    logPodEvent(logger, eventType, pod.Metadata?.NamespaceProperty, pod.Metadata?.Name, null);
});

await host.RunAsync();
