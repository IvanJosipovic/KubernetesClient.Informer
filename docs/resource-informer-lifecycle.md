# Resource informer lifecycle and logs

These diagrams cover `ResourceInformer<TResource>` itself. They do not include host startup or hosted-service adapter logs.

## `RunAsync`

`RunAsync` waits for `StartWatching()` before making API requests. The initial list completes before `ReadyAsync()` is signaled. Recoverable list/watch errors are handled inside the list-watch loop and do not normally change the status to `Retrying`.

```mermaid
flowchart TD
    A["RunAsync called<br/>Status = Starting"] --> B{"Start gate signaled?"}
    B -- "No" --> C["Wait for StartWatching()<br/>No log"]
    C --> D["StartWatching()<br/>Status = Starting; signal gate<br/>No log"]
    C -. "RunAsync token canceled" .-> AD
    D --> B
    B -- "Yes" --> E["RunListWatchAsync<br/>shouldSync = true; firstSync = true"]

    E --> F{"List required?"}
    F -- "Yes: initial list or expired resource version" --> G["ListAsync"]
    G --> H["Information · EventId 101<br/>SynchronizeStarted<br/>(selector variant when configured)"]
    H --> I["Fetch all list pages; build new cache;<br/>invoke callbacks for listed resources"]
    I --> J["Reconcile previously cached resources;<br/>invoke Deleted callbacks for missing items"]
    J --> K["Information · EventId 102<br/>SynchronizeComplete"]
    K --> L{"First successful list?"}
    L -- "Yes" --> M["Complete ReadyAsync waiters<br/>No log"]
    L -- "No" --> N["Continue list-watch loop"]
    M --> N
    F -- "No: resume watch from last version" --> N

    N --> O["Status = Running"]
    O --> P["WatchAsync"]
    P --> Q["Information · EventId 103<br/>WatchingResource<br/>includes resource version"]
    Q --> R{"Watch activity"}
    R -- "Added / Modified / Deleted" --> S["Debug · EventId 106<br/>InformerWatchEvent"]
    S --> T["Update cache and resource version;<br/>invoke callback"]
    T --> R
    R -- "Bookmark" --> U["Debug · EventId 106<br/>Update resource version;<br/>no resource callback"]
    U --> R

    R -- "Watch error callback" --> V["Error · EventId 104<br/>Received error while watching"]
    V --> W["If watch ends, return to loop"]
    R -- "Idle for more than 9.5 minutes" --> X["Debug · EventId 107<br/>DisposingToReconnect;<br/>cancel this watch"]
    X --> W
    R -- "Watch ends normally" --> W

    W --> Y["Rate-limit reconnect, then repeat loop"]
    Y --> F

    G -. "Caught I/O, HTTP, or Kubernetes exception" .-> Z["Debug · EventId 104<br/>ReceivedError"]
    P -. "Caught I/O, HTTP, or Kubernetes exception" .-> Z
    I -. "Callback throws" .-> AC
    J -. "Callback throws" .-> AC
    T -. "Callback throws" .-> AC
    G -. "RunAsync token canceled" .-> AD
    P -. "RunAsync token canceled" .-> AD
    Z --> AA{"Expired resource version?"}
    AA -- "Yes: set shouldSync = true" --> AB["Rate-limit retry"]
    AA -- "No: resume watch" --> AB
    AB --> F
    AB -. "RunAsync token canceled" .-> AD
    Y -. "RunAsync token canceled" .-> AD

    G -. "Unexpected exception, including callback failure" .-> AC["Status = Faulted;<br/>record exception; Error · EventId 109<br/>WatchingFailed; rethrow"]
    P -. "Unexpected exception" .-> AC
    R -- "RunAsync token canceled" --> AD["Status = Stopped;<br/>Information · EventId 105<br/>WatchingStopped; rethrow cancellation"]
    AC --> AE["RunAsync exits with exception"]
```

## `RunInfinite`

`RunInfinite` is a wrapper around repeated `RunAsync` calls. It calls `StartWatching()` itself. Most expected API/watch failures are retried inside `RunAsync`; the 10-second warning-and-delay path below applies when a retryable exception escapes that inner loop.

```mermaid
flowchart TD
    A["RunInfinite called"] --> B["Call StartWatching()<br/>Status = Starting; signal start gate<br/>No log"]
    B --> C["Call RunAsync"]
    C --> D["RunAsync executes its list-watch loop<br/>and handles expected transient errors internally"]
    D --> E{"RunAsync exits"}

    E -- "Returns without exception" --> F{"Cancellation requested?"}
    F -- "No" --> C
    F -- "Yes" --> G["finally: Status = Stopped"]

    E -- "Retryable exception escapes" --> H["RunAsync sets Faulted and logs<br/>Error · EventId 109 WatchingFailed;<br/>then rethrows"]
    H --> I{"RunInfinite retry filter matches?<br/>HTTP, Kubernetes, or socket I/O error;<br/>token not canceled"}
    I -- "Yes" --> J["Status = Retrying;<br/>Warning · EventId 110<br/>WatchingRetryScheduled"]
    J --> K["Wait 10 seconds"]
    K --> C
    I -- "No" --> L["Exception escapes RunInfinite"]
    L --> G

    E -- "OperationCanceledException and token canceled" --> M["RunAsync sets Stopped and logs<br/>Information · EventId 105 WatchingStopped;<br/>then rethrows cancellation"]
    M --> N["RunInfinite absorbs cancellation"]
    N --> G

    E -- "Other exception escapes" --> O["RunAsync logs Error · EventId 109<br/>WatchingFailed, then rethrows"]
    O --> L
```

## Log levels and operational criticality

| Level | Event | Interpretation |
|---|---|---|
| **Debug — diagnostic** | 106 `InformerWatchEvent` | Per-watch-event detail; potentially high volume. Initial-list items invoke callbacks but are not individually logged by the informer. |
| **Debug — usually recoverable** | 104 `ReceivedError` | A caught I/O, HTTP, or Kubernetes exception. The list-watch loop rate-limits and retries. |
| **Debug — expected recovery** | 107 `DisposingToReconnect` | The watch has been idle for over 9.5 minutes and is deliberately closed to trigger reconnection. |
| **Information — normal** | 101 `SynchronizeStarted`; 102 `SynchronizeComplete` | A list or relist starts and completes. |
| **Information — normal** | 103 `WatchingResource` | A watch opens; emitted again after reconnects. |
| **Information — normal stop** | 105 `WatchingStopped` | `RunAsync` observed cancellation from its supplied token. |
| **Warning — retry scheduled** | 110 `WatchingRetryScheduled` | `RunInfinite` will wait 10 seconds before retrying an escaping retryable exception. |
| **Error — may still recover** | 104 `ReceivedError` | The watch error callback reported an error. This log alone does not mean the informer stopped. The same event ID is also used at Debug level for caught loop errors. |
| **Error — failed run** | 109 `WatchingFailed` | An exception escaped `RunAsync`; its status becomes `Faulted` and the exception is rethrown. `RunInfinite` may retry matching exceptions. |

There are no Critical-level logs in `ResourceInformer<TResource>`. `StartWatching()`, callback invocation, and `ReadyAsync()` do not log directly.
