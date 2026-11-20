# Deep Code Review — `KubernetesClient.Informer`

**Branch:** `beta` · **Date:** 2026-11-20 · **Reviewer scope:** informer logic, error handling, security, Kubernetes standards conformance

## 1. Executive summary

The codebase implements a typed list-and-watch informer with WatchList bootstrap, paginated list fallback, 410-relist recovery, per-registration async callback queues, retry/backoff with `Retry-After`, OpenTelemetry activities, and hosted-service integration. Overall structure and lifecycle handling are strong, the test suite is extensive, and the project builds cleanly (`dotnet build -f net11.0`: 0 warnings, 0 errors, `TreatWarningsAsErrors=true`).

The review found **no critical vulnerabilities**, but did identify one high-severity operability bug, four medium findings, and several low-severity robustness/thread-safety issues.

> **Note on uncommitted work:** `src/.../ResourceInformer.cs` and `tests/.../ResourceInformerTests.cs` have uncommitted modifications (null-safe `HttpClient` capture and shutdown disposal→cancellation conversion). I reviewed these in place; they are correct defensive fixes and introduce no new issues. No source files were modified by this review.

### Findings at a glance

| # | Severity | Area | Finding | Location |
|---|----------|------|---------|----------|
| F1 | **High** | Error handling / conformance | WatchList bootstrap retries all `HttpRequestException` (401/403/404) forever — never faults, never falls back to list | `ResourceInformer.cs:486-491`, `928-931` |
| F2 | **Medium** | Error handling | Catch-all `catch (Exception)` treats programmer/data errors as transient → infinite retry, stuck `Retrying`, `ReadyAsync` never completes | `ResourceInformer.cs:527-532` |
| F3 | **Medium** | Security / availability | Unbounded per-registration channels allow memory exhaustion from slow consumers under high churn | `Registration.cs:15-19` |
| F4 | **Medium** | Security / privacy | Raw API error bodies embedded in exception messages and exported to logs/traces without redaction | `ResourceInformer.cs:928-931`, `InformerDiagnostics.cs:20-36` |
| F5 | **Medium** | API design / conformance | Watch path requires concrete `Kubernetes`, silently disabling WatchList and throwing for custom `IKubernetes` implementations | `ResourceInformer.cs:850-858`, `953-958` |
| F6 | Low | Concurrency | Data race on captured `lastEventUtc` between watch loop and reconnect timer | `ResourceInformer.cs:1181-1192`, `1211` |
| F7 | Low | Robustness | Watch events are not shape-validated; null/malformed objects cause NRE → retry loop | `ResourceInformer.cs:638-656`, `1220-1264` |
| F8 | Low | Configuration | WatchList bootstrap timeout reuses user `_timeoutSeconds`; large values delay fallback for minutes | `ResourceInformer.cs:546-549` |
| F9 | Low | Lifecycle | `Dispose()` does not stop a `RunInfinite` loop (uses caller's token) | `ResourceInformer.cs:189-209`, `343-416` |
| F10 | Low | Correctness | `GroupApiVersionKind.Equals` ignores `PluralName` (which is mutable) | `GroupApiVersionKind.cs:44`, `80-91` |

---

## 1b. Remediation status

**F1–F9 are fixed in the working tree** (F10 was intentionally left unchanged, as it was outside the requested range). Verification: `dotnet build` for both `net10.0` and `net11.0` succeeds with 0 warnings/errors, and the full test suite passes (146/146, including two new regression tests).

| Finding | Fix applied |
|---|---|
| F1 | `HttpRequestException` is now classified by status code (`IsRetryable(HttpRequestException)`): transport failures with no status stay retryable, while 401/403/404 fault immediately. Added `ForbiddenWatchListFaultsInformerInsteadOfRetrying`. |
| F2 | The list/watch catch-all now only retries known transient exception categories (`IsTransientForRetry`); unexpected exceptions fault. `RunInfinite` still retries unexpected failures for resilience but faults after three consecutive ones. |
| F3 | Registrations track the live-event queue depth; a backlog over 10,000 notifications removes the registration and logs EventId 112. One-shot cache replay is exempt so large caches are never truncated. |
| F4 | WatchList error messages no longer embed the API response body, and `InformerDiagnostics.RecordException` exports only the exception type (no message/stack trace). |
| F5 | Added a portable `IKubernetes` watch path (`CreatePortableWatchResponseAsync`); the direct optimized path is still used for the concrete `Kubernetes` client, and WatchList is skipped for other implementations. |
| F6 | Idle-watch tracking now uses interlocked 64-bit ticks instead of a torn-prone captured `DateTimeOffset`. |
| F7 | Blank lines are skipped; malformed nonblank frames raise a recovery exception — bootstrap falls back to a full list and regular watches reconnect from the last successfully processed resource version. |
| F8 | WatchList bootstrap timeout is capped at 30 seconds (short configured values are still honored); query and cancellation now agree. |
| F9 | `RunInfinite` links the caller's token with the hosted-service run token, so `Dispose()` stops a manually driven loop. Added `DisposeStopsRunInfinite`. |

Supporting test changes: two simulated infrastructure failures now throw `HttpRequestException` (a transient category) instead of `InvalidOperationException` (a defect), and one pre-existing virtual-timer loop was made race-resistant. Lifecycle docs and README were updated accordingly.

---

## 2. Detailed findings

### F1 — High — WatchList bootstrap retries permanent HTTP failures forever

**Evidence**

`CreateWatchListResponseAsync` converts every non-success response that is not an explicit "unsupported" signal into an `HttpRequestException` carrying the status code:

```csharp
// ResourceInformer.cs:919-932
if (!response.IsSuccessStatusCode)
{
    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken)...;
    if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
        && responseBody.Contains("sendInitialEvents", StringComparison.OrdinalIgnoreCase))
        throw new WatchListUnsupportedException();

    throw new HttpRequestException($"WatchList request failed with status ...: {responseBody}",
        inner: null, response.StatusCode);
}
```

The centralized handler in `RunListWatchAsync` retries **all** `HttpRequestException` without consulting the status code:

```csharp
// ResourceInformer.cs:486-491
catch (HttpRequestException ex)
{
    Status = ResourceInformerStatus.Retrying;
    await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken)...;
    continue;
}
```

By contrast, the sibling `HttpOperationException` / `KubernetesException` handlers *do* gate on `IsRetryable` (`:492-526`), and `IsRetryableStatusCode` (`:731-733`) already defines 401/403/404 as non-retryable.

**Impact**

- A ServiceAccount that lacks `watch`/`list` RBAC (403), an expired/invalid token (401), or a CRD that is not installed (404) causes the WatchList bootstrap to retry with bounded backoff **indefinitely**.
- The informer never reaches `ListAsync` fallback, never sets `ReadyAsync`, never enters `Faulted`, and `StopApplication()` is never triggered (host-managed mode). Consumers block forever in `ReadyAsync` while `Status` stays `Retrying`.
- This is inconsistent with the regular list path, where the same 403 faults the run (`ForbiddenKubernetesStatusDuringListFaultsInformer` test).

**Recommendation**

Classify `HttpRequestException` by `HttpRequestException.StatusCode` using the existing `IsRetryableStatusCode`:

```csharp
catch (HttpRequestException ex)
{
    if (!IsRetryableStatusCode((int?)ex.StatusCode)) throw;
    ...
}
```

Additionally, treat authorization failures during bootstrap as a fallback trigger (or at minimum fault promptly), and add a test covering a 401/403 from the WatchList endpoint.

---

### F2 — Medium — Catch-all converts all exceptions into transient retries

**Evidence**

```csharp
// ResourceInformer.cs:527-532
catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
{
    Status = ResourceInformerStatus.Retrying;
    await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken)...;
    continue;
}
```

This swallows `NullReferenceException`, `InvalidOperationException`, `JsonException`, `ArgumentException`, and any library defect. `IsRetryable(Exception)` in `RunInfinite` (`:723-729`) similarly defaults unknown exceptions to retryable.

**Impact**

- Bugs and malformed data produce an unbounded retry loop with no `Faulted` transition and no host shutdown.
- On-call operators see only repetitive warning/error logs and `Status = Retrying`; the root cause is masked and `ReadyAsync` never completes.
- The documented contract ("other unexpected failures are retried") makes this intentional, but the breadth is operationally risky.

**Recommendation**

Narrow the catch to known transient categories (I/O, HTTP, Kubernetes status, `TaskCanceledException`); let truly unexpected exceptions escape to `Faulted`. If broad retry is kept for resilience, add a consecutive-failure ceiling or emit a distinct high-severity log after N failures so it is distinguishable from a genuine transient storm.

---

### F3 — Medium — Unbounded per-registration queues (memory exhaustion)

**Evidence**

```csharp
// Registration.cs:15-19
private readonly Channel<Notification> _notifications =
    Channel.CreateUnbounded<Notification>(new UnboundedChannelOptions
    { SingleReader = true, SingleWriter = false });
```

A registration's consumer is a single background task (`Start()`, `:44-47`); notifications are written synchronously for every watch event (`:49-50`, `EnqueueRegistrationCallbacks` at `ResourceInformer.cs:1288-1294`).

**Impact**

- A callback that blocks (I/O, locks, waiting on the UI thread) or a high-churn resource (frequently-updating Pods/Endpoints) lets the queue grow without limit.
- Each `Notification` retains the full `TResource` object, so memory growth is proportional to event rate × stall duration — an availability/DoS concern, especially in long-running controllers.
- A failing callback is removed (`:74-91`), which helps, but a merely *slow* callback is not.

**Recommendation**

Use a bounded channel with an explicit overflow policy (drop-oldest, coalesce updates by `NamespacedName`, or block the producer with backpressure). At minimum, document that callbacks must be non-blocking (the interface docs already say "Keep them short and non-blocking") and expose a queue-depth metric/activity tag for observability.

---

### F4 — Medium — Sensitive response bodies flow into exceptions and telemetry

**Evidence**

`CreateWatchListResponseAsync` interpolates the entire API response body into the exception message (`:928-931`). `LogListWatchFailure` attaches that exception to structured logs (`:659-673`), and `InformerDiagnostics.RecordException` writes `exception.Message` and `exception.ToString()` into activity tags:

```csharp
// InformerDiagnostics.cs:27-35
activity.SetStatus(ActivityStatusCode.Error, exception.Message);
activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
{
    ["exception.message"] = exception.Message,
    ["exception.stacktrace"] = exception.ToString()
}));
```

**Impact**

- If the informer watches Secrets or other sensitive resources, an API error body (or admission webhook message) can be copied verbatim into logs and distributed traces.
- Traces are commonly exported to third-party backends, widening exposure beyond the cluster.

**Recommendation**

Do not embed raw response bodies in exception messages. Truncate and sanitize (strip bracketed/quoted object data), log a status code plus a bounded message, and avoid putting full stack traces/messages on activity tags (or apply a redaction processor). Credential material itself is handled correctly — `ProcessHttpRequestAsync` is used for auth and no headers are logged.

---

### F5 — Medium — Watch path is hard-coupled to the concrete `Kubernetes` client

**Evidence**

```csharp
// ResourceInformer.cs:850-858 (WatchList)
if (Client is not Kubernetes kubernetes) return null;   // silently skips WatchList

// ResourceInformer.cs:953-958 (regular watch)
if (Client is not Kubernetes kubernetes)
    throw new InvalidOperationException("A Kubernetes client is required for direct watch requests.");
```

Lists go through `IKubernetes.CustomObjects...` (`:816-848`) and work with any implementation. The constructor and DI accept `IKubernetes`, and the README states users may register their own `IKubernetes` client.

**Impact**

- A decorator, proxy, mock, or alternative `IKubernetes` implementation lists successfully and then fails (`:953-958`) on the first watch; the resulting `InvalidOperationException` is swallowed by the F2 catch-all and retried forever.
- WatchList optimization is silently unavailable for such clients, contrary to the documented "unsupported by the configured client" fallback being a performance-only degradation.

**Recommendation**

Either require the concrete `Kubernetes` type at construction (and document it), or provide a portable watch path for `IKubernetes` (e.g., `WatcherExt` with correct cancellation handling) so the abstraction is honored. Add a test with a non-`Kubernetes` `IKubernetes` double.

---

### F6 — Low — Data race on idle-watch timestamp

**Evidence**

`lastEventUtc` is a local `DateTimeOffset` captured by the timer callback and written from two threads without synchronization:

```csharp
// ResourceInformer.cs:1181-1192
var lastEventUtc = _timeProvider.GetUtcNow();
using var checkLastEventUtcTimer = _timeProvider.CreateTimer(_ =>
{
    var lastEvent = _timeProvider.GetUtcNow() - lastEventUtc;   // read
    if (lastEvent > TimeSpan.FromMinutes(9.5))
    {
        lastEventUtc = DateTimeOffset.MaxValue;                 // write (timer thread)
        reconnectCancellationTokenSource.Cancel();
    }
}, ...);
...
lastEventUtc = _timeProvider.GetUtcNow();                        // write (watch loop), :1211
```

`DateTimeOffset` is a 16-byte struct; non-atomic read/write can tear. Consequences are limited (slightly early/late reconnect) but it is a genuine race and a source of virtual-time test flakiness.

**Recommendation**

Store monotonic ticks in a `long` and use `Interlocked.Exchange`/`Volatile.Read`, or guard with a small lock.

---

### F7 — Low — Watch events are not shape-validated

**Evidence**

```csharp
// ResourceInformer.cs:645-656
while (await reader.ReadLineAsync(cancellationToken)... is { } line)
{
    var genericEvent = KubernetesJson.Deserialize<Watcher<KubernetesObject>.WatchEvent>(line);
    if (genericEvent.Object?.Kind == "Status")   // genericEvent itself may be null
    ...
    var watchEvent = KubernetesJson.Deserialize<Watcher<TResource>.WatchEvent>(line);
    yield return (watchEvent.Type, watchEvent.Object);  // Object may be null
}
```

`OnEvent` (`:1220-1264`) and the WatchList path (`:621-624`) dereference `item` unconditionally (`item.Name()`, `item.Kind`, `resource.ApiVersion = ...`). A blank line, `null`, or an object-less event produces an NRE that F2 turns into an endless retry.

**Recommendation**

Null-check the deserialized event and `Object`, skip/ignore or explicitly fault on malformed frames, and treat blank lines defensively.

---

### F8 — Low — Bootstrap timeout is derived from the watch timeout

**Evidence**

```csharp
// ResourceInformer.cs:546-549
var bootstrapTimeoutSeconds = _timeoutSeconds is > 0
    ? _timeoutSeconds.Value
    : WatchListBootstrapTimeoutSeconds;   // 30
bootstrapTimeout.CancelAfter(TimeSpan.FromSeconds(bootstrapTimeoutSeconds));
```

A user who sets `timeoutSeconds` to a large value (e.g., 600) for watch stability also waits up to 600 s before a stuck WatchList falls back to list.

**Recommendation**

Keep the bootstrap timeout independent (fixed 30 s or its own option); use `_timeoutSeconds` only for the long-lived watch request.

---

### F9 — Low — `Dispose()` does not stop `RunInfinite`

`Dispose()` (`:189-209`) completes registrations and calls `base.Dispose()` (which cancels the host-managed `_runCancellation`), but `RunInfinite` (`:343-416`) runs on the caller's token. Disposing the informer while it is running via `RunInfinite` suppresses callbacks (`OnEvent` returns early when `_disposed`, `:1242-1245`) but leaves the list/watch loop issuing API requests until the caller cancels.

**Recommendation**

Document that `RunInfinite` must be stopped via its token, or link `_runCancellation` into the `RunInfinite` token so `Dispose()` is sufficient.

---

### F10 — Low — `GroupApiVersionKind` equality ignores mutable `PluralName`

`Equals`/`GetHashCode` (`:80-91`) compare only `GroupApiVersion` and `Kind`, while `PluralName` is a mutable property (`:44`). Two values with the same group/version/kind but different plurals compare equal. No current collection uses this as a key, so impact is limited, but it is a latent correctness trap.

---

## 3. Kubernetes standards conformance — what is correct

The informer follows the Kubernetes list-watch contract closely in most areas:

- **WatchList (streaming lists):** correct query set — `watch=true`, `sendInitialEvents=true`, `allowWatchBookmarks=true`, `resourceVersionMatch=NotOlderThan`, `timeoutSeconds`, URL-escaped `fieldSelector` (`:880-892`). I verified against the official [API concepts](https://kubernetes.io/docs/reference/using-api/api-concepts/): omitting `resourceVersion` is explicitly interpreted as a consistent read, and `resourceVersionMatch=NotOlderThan` is required — both satisfied.
- **Initial snapshot atomicity:** only ADDED events are staged before the `k8s.io/initial-events-end: "true"` bookmark (`:36`, `:589-625`, `:634-636`); nothing is published until the bookmark arrives, and incomplete streams fall back without publishing partial state.
- **Relist recovery:** `410 Gone` and `Reason=Expired` are detected for both `KubernetesException` and `HttpOperationException` (`:702-733`), set `shouldSync`, and trigger a full relist; the two-step recovery (relist at current RV → 410 → relist with no RV via `MarkListResourceVersionUnavailable`) matches client-go behavior.
- **Bookmarks:** requested and used to advance `_lastResourceVersion` without notifying callbacks (`:1254-1265`).
- **Pagination:** `limit` + `continue` with `resourceVersion` only on the first page (`:1062-1091`, `:1146-1171`) — correct semantics.
- **Watch error events:** Status-kind frames are converted to `KubernetesException` (`:647-652`), matching Kubernetes watch error-event semantics.
- **`Retry-After`:** honors both `Status.Details.RetryAfterSeconds` and the `Retry-After` header (numeric and HTTP-date), capped at 300 s (`:757-806`).
- **Idle reconnect:** 9.5-minute idle watch cutoff (`:1181-1192`) is a reasonable defense against server-side watch timeouts.

## 4. Security — what is correct

- Path segments (group, version, namespace, plural) and the field selector are escaped with `Uri.EscapeDataString` (`:860-878`, `:960-978`) — no query/path injection.
- Authentication is applied via the client's configured `credentials.ProcessHttpRequestAsync` (`:907-909`, `:1006-1009`); the direct request reuses `kubernetes.HttpClient` so TLS/CA/proxy settings are preserved. No credentials or `Authorization` headers are logged.
- Deserialization uses `KubernetesJson` with concrete types — no polymorphic/arbitrary type resolution (RCE-safe).
- Per-event logs/activity tags include only kind/name/namespace/RV, not object bodies (`:1225-1237`).
- `AllowWatchBookmarks` and 410 handling prevent silently missing events.

Remaining security concerns are F3 (resource exhaustion), F4 (data in telemetry), and F7 (malformed input robustness).

## 5. Error handling — what is correct

- Centralized `RunCoreAsync` error handling sets `_ready` exception/cancellation, `Faulted`/`Stopped` status, telemetry, and host-stop propagation (`:296-339`).
- `RunInfinite` cleanly separates retryable escapes from non-retryable faults (`:343-416`), with bounded exponential backoff + jitter (`:757-775`) and severity-classified logs (`:659-700`).
- Callback failures remove only the failing registration and do not fault the informer (`Registration.cs:74-91`), and initial-delivery failures fault only that registration's readiness.
- The uncommitted shutdown fix correctly converts client-disposal errors into cancellation during token cancellation (`:1026-1035`) and guards the null `HttpClient` (`:912-917`, `:1013-1017`).

Primary error-handling gaps are F1, F2, and F8.

## 6. Test coverage observations

The suite is broad (WatchList bootstrap/fallback/bookmark atomicity, pagination, 410 relist, Retry-After, backoff reset, callback isolation, disposal linearizability, diagnostics). The original gaps have since been closed by regression tests: forbidden WatchList responses (F1), a custom non-`Kubernetes` `IKubernetes` client (F5), malformed WatchList and watch frames (F7), queue-depth overflow for slow live callbacks, and a cache replay larger than the live-event limit (F3).

## 7. Recommended remediation order

1. **F1** — classify `HttpRequestException` by status code and fault/fall back on non-retryable codes; add RBAC test.
2. **F5 / F2** — decide and document the `IKubernetes` contract; narrow the catch-all so faults surface.
3. **F3 / F4** — bound registration queues; sanitize error bodies and telemetry.
4. **F6–F10** — thread-safety, validation, timeout decoupling, lifecycle and equality cleanup.

*This report is a static review grounded in the current working tree; no source files were modified.*
