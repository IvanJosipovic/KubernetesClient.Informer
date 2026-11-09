// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KubernetesClient.Informer.Hosting;
using KubernetesClient.Informer.Rate;
using System.ComponentModel;

namespace KubernetesClient.Informer.Client;

/// <summary>
/// Class ResourceInformer.
/// Implements the <see cref="IResourceInformer{TResource}" />.
/// Implements the <see cref="IDisposable" />.
/// </summary>
/// <typeparam name="TResource">The type of the t resource.</typeparam>
/// <seealso cref="IResourceInformer{TResource}" />
/// <seealso cref="IDisposable" />
public partial class ResourceInformer<TResource> : BackgroundHostedService, INotifyPropertyChanged, IResourceInformer<TResource>
    where TResource : class, IKubernetesObject<V1ObjectMeta>, new()
{
    private readonly object _sync = new();
    private readonly GroupApiVersionKind _groupApiVersionKind;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ResourceSelector<TResource>? _selector;
    private ImmutableList<Registration> _registrations = [];
    private Dictionary<NamespacedName, TResource> _cache = [];
    private string? _lastResourceVersion;
    private readonly string? _namespace;
    private int _status = (int)ResourceInformerStatus.NotStarted;
    private int? _timeoutSeconds;
    private readonly int _resourceListLimit;
    private bool _disposed;
    private bool _initialListStarted;
    private int _runActive;
    private int _runInfiniteActive;
    private readonly AsyncLocal<bool> _runInfiniteInvocation = new();

    /// <summary>
    /// Occurs when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public ResourceInformerStatus Status
    {
        get => (ResourceInformerStatus)Volatile.Read(ref _status);
        private set
        {
            if (Interlocked.Exchange(ref _status, (int)value) == (int)value)
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceInformer{TResource}" /> class.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="hostApplicationLifetime">The host application lifetime.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="selector">A resource selector for (optionally) filtering the list of resources.</param>
    /// <param name="namespace">The Namespace to scope the informer.</param>
    /// <param name="timeoutSeconds">The timeout in seconds for list and watch operations.</param>
    /// <param name="resourceListLimit">The maximum number of resources to request in each list page.</param>
    /// <param name="groupApiVersionKind">The resource group, API version, kind, and plural name to use when accessing the Kubernetes API.</param>
    public ResourceInformer(
        IKubernetes client,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<ResourceInformer<TResource>> logger,
        ResourceSelector<TResource>? selector = null,
        string? @namespace = null,
        int resourceListLimit = 1000,
        int? timeoutSeconds = null,
        GroupApiVersionKind? groupApiVersionKind = null)
        : base(hostApplicationLifetime, logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resourceListLimit);

        Client = client;
        _selector = selector;
        _namespace = @namespace;
        _groupApiVersionKind = groupApiVersionKind ?? GroupApiVersionKind.From<TResource>();
        _timeoutSeconds = timeoutSeconds;
        _resourceListLimit = resourceListLimit;
    }

    private enum EventType
    {
        SynchronizeStarted = 101,
        SynchronizeComplete = 102,
        WatchingResource = 103,
        ReceivedError = 104,
        WatchingStopped = 105,
        InformerWatchEvent = 106,
        DisposingToReconnect = 107,
        IgnoringError = 108,
        WatchingFailed = 109,
        WatchingRetryScheduled = 110,
    }

    protected IKubernetes Client { get; init; }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_sync)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _registrations = [];
                }
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public void StartWatching()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Status == ResourceInformerStatus.NotStarted)
            {
                Status = ResourceInformerStatus.Starting;
            }

            _start.TrySetResult();
        }
    }

    /// <inheritdoc/>
    public virtual IResourceInformerRegistration Register(ResourceInformerCallback<TResource> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return new Registration(this, callback);
    }

    /// <inheritdoc/>
    public IResourceInformerRegistration Register(ResourceInformerCallback<IKubernetesObject<V1ObjectMeta>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return new Registration(this, (eventType, resource) => callback(eventType, resource));
    }

    /// <inheritdoc/>
    public virtual async Task ReadyAsync(CancellationToken cancellationToken)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_runActive != 0 || (_runInfiniteActive != 0 && !_runInfiniteInvocation.Value))
            {
                throw new InvalidOperationException("The informer is already running.");
            }

            _runActive = 1;
        }

        var isRunInfiniteInvocation = _runInfiniteInvocation.Value;

        try
        {
            await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _runActive = 0;
            }
        }

        async Task RunCoreAsync(CancellationToken runCancellationToken)
        {
            using var activity = InformerDiagnostics.ActivitySource.StartActivity(nameof(RunAsync), ActivityKind.Consumer);
            SetResourceTags(activity);
            Status = ResourceInformerStatus.Starting;

            try
            {
                await _start.Task.WaitAsync(runCancellationToken).ConfigureAwait(false);

                await RunListWatchAsync(runCancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (runCancellationToken.IsCancellationRequested)
            {
                if (!isRunInfiniteInvocation)
                {
                    _ready.TrySetCanceled(runCancellationToken);
                }

                Status = ResourceInformerStatus.Stopped;
                Logger.LogInformation(
                    EventId(EventType.WatchingStopped),
                    "Stopped watching {ResourceType} resources from API server.",
                    typeof(TResource).Name);
                throw;
            }
            catch (Exception error)
            {
                if (!isRunInfiniteInvocation)
                {
                    _ready.TrySetException(error);
                }

                Status = ResourceInformerStatus.Faulted;
                InformerDiagnostics.RecordException(activity, error);
                Logger.LogError(
                    EventId(EventType.WatchingFailed),
                    error,
                    "Failed while watching {ResourceType} resources from API server.",
                    typeof(TResource).Name);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public async Task RunInfinite(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_runActive != 0 || _runInfiniteActive != 0)
            {
                throw new InvalidOperationException("The informer is already running.");
            }

            _runInfiniteActive = 1;
        }

        try
        {
            StartWatching();
            var retryAttempt = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _runInfiniteInvocation.Value = true;
                    try
                    {
                        await RunAsync(cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _runInfiniteInvocation.Value = false;
                    }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && IsRetryable(error))
                {
                    Status = ResourceInformerStatus.Retrying;
                    var retryDelay = GetRetryDelay(error, retryAttempt++);

                    Logger.LogWarning(
                        EventId(EventType.WatchingRetryScheduled),
                        "Retrying {ResourceType} in {RetryDelay} after a transient failure.",
                        typeof(TResource).Name,
                        retryDelay);

                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            _ready.TrySetCanceled(cancellationToken);
            Status = ResourceInformerStatus.Stopped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(cancellationToken);
            Status = ResourceInformerStatus.Stopped;
        }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            Status = ResourceInformerStatus.Faulted;
            throw;
        }
        finally
        {
            _runInfiniteInvocation.Value = false;
            lock (_sync)
            {
                _runInfiniteActive = 0;
            }
        }
    }

    private async Task RunListWatchAsync(CancellationToken cancellationToken)
    {
        var limiter = new Limiter(new Limit(0.2), 3);
        var shouldSync = true;
        var firstSync = true;
        var retryAttempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (shouldSync)
                {
                    lock (_sync)
                    {
                        _initialListStarted = true;
                    }

                    await ListAsync(cancellationToken).ConfigureAwait(true);
                    shouldSync = false;
                }

                if (firstSync)
                {
                    _ready.TrySetResult();
                    firstSync = false;
                }

                Status = ResourceInformerStatus.Running;
                await WatchAsync(cancellationToken).ConfigureAwait(true);
                Status = ResourceInformerStatus.Retrying;
                retryAttempt = 0;
            }
            catch (IOException ex) when (ex.InnerException is SocketException)
            {
                LogWatchError(ex);
                Status = ResourceInformerStatus.Retrying;
                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogWatchError(ex);
                Status = ResourceInformerStatus.Retrying;
                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                LogWatchError(ex);
                Status = ResourceInformerStatus.Retrying;
                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpOperationException ex) when (IsRetryable(ex))
            {
                LogWatchError(ex);
                Status = ResourceInformerStatus.Retrying;

                if (IsExpiredResourceVersion(ex))
                {
                    shouldSync = true;
                }

                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (KubernetesException ex) when (IsRetryable(ex))
            {
                LogWatchError(ex);
                Status = ResourceInformerStatus.Retrying;

                // Resource version expiry requires a full re-sync so no updates are missed.
                if (IsExpiredResourceVersion(ex))
                {
                    shouldSync = true;
                }

                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // rate limiting the reconnect loop
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private static EventId EventId(EventType eventType) => new EventId((int)eventType, eventType.ToString());

    private void LogWatchError(Exception error)
    {
        InformerDiagnostics.RecordException(Activity.Current, error);
        Logger.LogDebug(
            EventId(EventType.ReceivedError),
            "Received error watching {ResourceType}: {ErrorMessage}",
            typeof(TResource).Name,
            error);
    }

    private static bool IsExpiredResourceVersion(KubernetesException exception)
        => exception.Status?.Code == (int)HttpStatusCode.Gone
            || string.Equals(exception.Status?.Reason, "Expired", StringComparison.Ordinal);

    private static bool IsExpiredResourceVersion(HttpOperationException exception)
        => exception.Response?.StatusCode == HttpStatusCode.Gone;

    private static bool IsRetryable(Exception exception)
        => exception switch
        {
            TaskCanceledException => true,
            HttpRequestException => true,
            HttpOperationException httpOperationException => IsRetryableHttpStatus(httpOperationException.Response?.StatusCode),
            KubernetesException kubernetesException => IsRetryableKubernetesStatus(kubernetesException.Status),
            IOException { InnerException: SocketException } => true,
            _ => false,
        };

    private static bool IsRetryableKubernetesStatus(V1Status? status)
    {
        if (status is null)
        {
            return true;
        }

        if (status.Code is { } code)
        {
            return IsRetryableHttpStatus((HttpStatusCode)code);
        }

        return string.Equals(status.Reason, "Expired", StringComparison.Ordinal)
            || string.Equals(status.Reason, "Timeout", StringComparison.Ordinal)
            || string.Equals(status.Reason, "ServerTimeout", StringComparison.Ordinal)
            || string.Equals(status.Reason, "TooManyRequests", StringComparison.Ordinal)
            || string.Equals(status.Reason, "InternalError", StringComparison.Ordinal)
            || string.Equals(status.Reason, "ServiceUnavailable", StringComparison.Ordinal);
    }

    private static bool IsRetryableHttpStatus(HttpStatusCode? statusCode)
        => statusCode is HttpStatusCode.Gone or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || statusCode is { } code && (int)code is >= 500 and <= 599;

    private static TimeSpan GetRetryDelay(Exception exception, int attempt)
    {
        const double initialSeconds = 1;
        const double maximumBackoffSeconds = 60;
        const double maximumRetryAfterSeconds = 300;

        var backoffSeconds = Math.Min(initialSeconds * Math.Pow(2, Math.Min(attempt, 6)), maximumBackoffSeconds);
        var jitteredBackoff = TimeSpan.FromSeconds(backoffSeconds * (0.5 + Random.Shared.NextDouble()));
        var retryAfter = GetRetryAfter(exception);

        if (retryAfter is null || retryAfter <= jitteredBackoff)
        {
            return jitteredBackoff;
        }

        return retryAfter.Value > TimeSpan.FromSeconds(maximumRetryAfterSeconds)
            ? TimeSpan.FromSeconds(maximumRetryAfterSeconds)
            : retryAfter.Value;
    }

    private static TimeSpan? GetRetryAfter(Exception exception)
    {
        if (exception is KubernetesException kubernetesException
            && kubernetesException.Status?.Details?.RetryAfterSeconds is { } retryAfterSeconds
            && retryAfterSeconds >= 0)
        {
            return TimeSpan.FromSeconds(retryAfterSeconds);
        }

        if (exception is not HttpOperationException httpOperationException
            || httpOperationException.Response?.Headers is not { } headers
            || !headers.TryGetValue("Retry-After", out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var retryAt))
        {
            var delay = retryAt - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static Task WaitBeforeRetryAsync(Exception exception, int attempt, CancellationToken cancellationToken)
        => Task.Delay(GetRetryDelay(exception, attempt), cancellationToken);

    private Task<HttpOperationResponse<TResponse>> CreateResponseAsync<TResponse>(bool? watch, int? limit, bool? allowWatchBookmarks, string? continueParameter, string? resourceVersion, CancellationToken cancellationToken)
        where TResponse : class
    {
        if (string.IsNullOrEmpty(_namespace))
        {
            return Client.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync<TResponse>(
                _groupApiVersionKind.Group,
                _groupApiVersionKind.ApiVersion,
                _groupApiVersionKind.PluralName,
                allowWatchBookmarks: allowWatchBookmarks,
                continueParameter: continueParameter,
                fieldSelector: _selector?.FieldSelector,
                watch: watch,
                limit: limit,
                resourceVersion: resourceVersion,
                timeoutSeconds: _timeoutSeconds,
                cancellationToken: cancellationToken);
        }

        return Client.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync<TResponse>(
            _groupApiVersionKind.Group,
            _groupApiVersionKind.ApiVersion,
            _namespace,
            _groupApiVersionKind.PluralName,
            allowWatchBookmarks: allowWatchBookmarks,
            continueParameter: continueParameter,
            fieldSelector: _selector?.FieldSelector,
            watch: watch,
            limit: limit,
            resourceVersion: resourceVersion,
            timeoutSeconds: _timeoutSeconds,
            cancellationToken: cancellationToken);
    }

    private async Task ListAsync(CancellationToken cancellationToken)
    {
        using var activity = InformerDiagnostics.ActivitySource.StartActivity(nameof(ListAsync), ActivityKind.Client);
        SetResourceTags(activity);
        activity?.SetTag("k8s.informer.resource_version", _lastResourceVersion);

        var previousCache = _cache;
        var nextCache = new Dictionary<NamespacedName, TResource>();
        var listedItems = new List<(WatchEventType EventType, TResource Resource)>();
        string? nextResourceVersion = null;

        if (_selector?.FieldSelector is not null)
        {
            Logger.LogInformation(
                EventId(EventType.SynchronizeStarted),
                "Started synchronizing {ResourceType} resources from API server with field selector '{FieldSelector}'.",
                typeof(TResource).Name,
                _selector.FieldSelector);
        }
        else
        {
            Logger.LogInformation(
                EventId(EventType.SynchronizeStarted),
                "Started synchronizing {ResourceType} resources from API server.",
                typeof(TResource).Name);
        }

        string? continueParameter = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            // request next page of items
            using var listWithHttpMessage = await CreateResponseAsync<KubernetesList<TResource>>(
                watch: null,
                limit: _resourceListLimit,
                allowWatchBookmarks: null,
                continueParameter: continueParameter,
                resourceVersion: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var list = listWithHttpMessage.Body;
            foreach (var item in list.Items)
            {
                // These properties are not already set on items while listing
                // assigned here for consistency
                item.ApiVersion = _groupApiVersionKind.GroupApiVersion;
                item.Kind = _groupApiVersionKind.Kind;

                var key = NamespacedName.From(item);
                nextCache[key] = item;

                var watchEventType = WatchEventType.Added;
                if (previousCache.ContainsKey(key))
                {
                    // an already-known key is provided as a modification for re-sync purposes
                    watchEventType = WatchEventType.Modified;
                }

                listedItems.Add((watchEventType, item));
            }

            // keep track of values needed for next page and to start watching
            nextResourceVersion = list.ResourceVersion();
            continueParameter = list.Continue();
        }
        while (!string.IsNullOrEmpty(continueParameter));

        _cache = nextCache;
        _lastResourceVersion = nextResourceVersion;

        foreach (var (eventType, resource) in listedItems)
        {
            InvokeRegistrationCallbacks(eventType, resource);
        }

        foreach (var (key, item) in previousCache)
        {
            if (nextCache.ContainsKey(key))
            {
                continue;
            }

            // Reuse the last-known object so delete handlers retain the resource's full state.
            InvokeRegistrationCallbacks(WatchEventType.Deleted, item);
        }

        Logger.LogInformation(
            EventId(EventType.SynchronizeComplete),
            "Completed synchronizing {ResourceType} resources from API server.",
            typeof(TResource).Name);
        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("k8s.informer.resource_count", _cache.Count);
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var activity = InformerDiagnostics.ActivitySource.StartActivity(nameof(WatchAsync), ActivityKind.Client);
        SetResourceTags(activity);
        activity?.SetTag("k8s.informer.resource_version", _lastResourceVersion);

        Logger.LogInformation(EventId(EventType.WatchingResource), "Watching {ResourceType} starting from resource version {ResourceVersion}.", typeof(TResource).Name, _lastResourceVersion);

        var lastEventUtc = DateTime.UtcNow;
        using var reconnectCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var checkLastEventUtcTimer = new Timer(_ =>
        {
            var lastEvent = DateTime.UtcNow - lastEventUtc;
            if (lastEvent > TimeSpan.FromMinutes(9.5))
            {
                lastEventUtc = DateTime.MaxValue;
                Logger.LogDebug(EventId(EventType.DisposingToReconnect), "Disposing watcher for {ResourceType} to cause reconnect.", typeof(TResource).Name);
                reconnectCancellationTokenSource.Cancel();
            }
        }, state: null, dueTime: TimeSpan.FromSeconds(45), period: TimeSpan.FromSeconds(45));

        var watchWithHttpMessage = CreateResponseAsync<TResource>(
            watch: true,
            limit: null,
            allowWatchBookmarks: true,
            continueParameter: null,
            resourceVersion: _lastResourceVersion,
            cancellationToken: reconnectCancellationTokenSource.Token);

#pragma warning disable CS0618 // Type or member is obsolete
        try
        {
            await foreach (var (eventType, resource) in watchWithHttpMessage.WatchAsync<TResource, TResource>((e) =>
#pragma warning restore CS0618 // Type or member is obsolete
            {
                Logger.LogError(
                    EventId(EventType.ReceivedError),
                    e,
                    "Received error while watching {ResourceType} resources from API server.",
                    typeof(TResource).Name);

                ExceptionDispatchInfo.Capture(e).Throw();
            }, reconnectCancellationTokenSource.Token))
            {
                lastEventUtc = DateTime.UtcNow;
                OnEvent(eventType, resource);
            }
        }
        catch (OperationCanceledException) when (reconnectCancellationTokenSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnEvent(WatchEventType watchEventType, TResource item)
    {
        using var activity = InformerDiagnostics.ActivitySource.StartActivity(nameof(OnEvent), ActivityKind.Internal);
        SetResourceTags(activity);
        activity?.SetTag("k8s.informer.event_type", watchEventType.ToString());
        activity?.SetTag("k8s.resource.name", item.Name());
        activity?.SetTag("k8s.resource.namespace", item.Namespace());
        activity?.SetTag("k8s.resource.version", item.ResourceVersion());

        Logger.LogDebug(
            EventId(EventType.InformerWatchEvent),
            "Informer {ResourceType} received {WatchEventType} notification for {ItemKind}/{ItemName}.{ItemNamespace} at resource version {ResourceVersion}",
            typeof(TResource).Name,
            watchEventType,
            item.Kind,
            item.Name(),
            item.Namespace(),
            item.ResourceVersion());

        if (watchEventType == WatchEventType.Added ||
            watchEventType == WatchEventType.Modified)
        {
            // BUGBUG: log warning if cache was not in expected state
            _cache[NamespacedName.From(item)] = item;
        }

        if (watchEventType == WatchEventType.Deleted)
        {
            _cache.Remove(NamespacedName.From(item));
        }

        if (watchEventType == WatchEventType.Added ||
            watchEventType == WatchEventType.Modified ||
            watchEventType == WatchEventType.Deleted ||
            watchEventType == WatchEventType.Bookmark)
        {
            _lastResourceVersion = item.ResourceVersion();
        }

        if (watchEventType == WatchEventType.Added ||
            watchEventType == WatchEventType.Modified ||
            watchEventType == WatchEventType.Deleted)
        {
            InvokeRegistrationCallbacks(watchEventType, item);
        }
    }

    private void SetResourceTags(Activity? activity)
    {
        activity?.SetTag("k8s.resource.type", typeof(TResource).Name);
        activity?.SetTag("k8s.group", _groupApiVersionKind.Group);
        activity?.SetTag("k8s.version", _groupApiVersionKind.ApiVersion);
        activity?.SetTag("k8s.resource.kind", _groupApiVersionKind.Kind);
        activity?.SetTag("k8s.resource.plural", _groupApiVersionKind.PluralName);
        activity?.SetTag("k8s.namespace.name", _namespace);
    }

    private void InvokeRegistrationCallbacks(WatchEventType eventType, TResource resource)
    {
        List<Exception>? innerExceptions = default;
        ImmutableList<Registration> registrations;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            registrations = _registrations;
        }

        foreach (var registration in registrations)
        {
            try
            {
                registration.Callback.Invoke(eventType, resource);
            }
            catch (Exception innerException)
            {
                innerExceptions ??= [];
                innerExceptions.Add(innerException);
            }
        }

        if (innerExceptions is not null)
        {
            throw new AggregateException("One or more exceptions thrown by ResourceInformerCallback.", innerExceptions);
        }
    }
}
