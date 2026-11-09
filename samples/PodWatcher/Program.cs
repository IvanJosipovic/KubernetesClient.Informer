using k8s.Models;
using KubernetesClient.Informer.Client;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddKubernetesCore();
builder.Services.RegisterResourceInformer<V1Pod>();

var host = builder.Build();

var podInformer = host.Services.GetRequiredService<IResourceInformer<V1Pod>>();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PodWatcher");

using var registration = podInformer.Register((eventType, pod) =>
{
    logger.LogInformation("{EventType}: {Namespace}/{PodName}", eventType, pod.Metadata?.NamespaceProperty, pod.Metadata?.Name);
});

await host.RunAsync();
