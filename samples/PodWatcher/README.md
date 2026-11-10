# Pod watcher sample

A .NET Worker Service that subscribes to Pod add, modify, and delete notifications using `KubernetesClient.Informer`.

Run it from the repository root:

```sh
dotnet run --project samples/PodWatcher
```

The sample uses the Worker Service hosting model. `RegisterResourceInformer<V1Pod>()` registers the informer with the host, which starts and stops it automatically. Events are logged through `ILogger`. The sample uses `AddKubernetesCore()` and the default Kubernetes client configuration: local kubeconfig when available, or in-cluster configuration when deployed in Kubernetes. The initial Pod list is logged as `Added` events; subsequent watch events are logged as they arrive. Press Ctrl+C to stop the host and informer.

To coordinate informer startup manually, register it with `startWatchingOnHostStart: false`, then call `StartWatching()` after the other startup work is ready.
