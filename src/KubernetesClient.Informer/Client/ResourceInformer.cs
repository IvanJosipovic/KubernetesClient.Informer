// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Net.Sockets;
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
    private readonly GroupApiVersionKind _names;
    private readonly SemaphoreSlim _ready = new(0);
    private readonly SemaphoreSlim _start = new(0);
    private int _startWaited;
    private readonly ResourceSelector<TResource>? _selector;
    private ImmutableList<Registration> _registrations = [];
    private Dictionary<NamespacedName, IList<V1OwnerReference>> _cache = [];
    private string? _lastResourceVersion;
    private readonly string? _namespace;
    private ResourceInformerStatus _status = ResourceInformerStatus.NotStarted;
    private int? _timeoutSeconds;
    private readonly int _resourceListLimit;

    /// <summary>
    /// Occurs when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public ResourceInformerStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
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
    public ResourceInformer(
        IKubernetes client,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<ResourceInformer<TResource>> logger,
        ResourceSelector<TResource>? selector = null,
        string? @namespace = null,
        int? timeoutSeconds = null,
        int resourceListLimit = 1000)
        : base(hostApplicationLifetime, logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resourceListLimit);

        Client = client;
        _selector = selector;
        _namespace = @namespace;
        _names = GroupApiVersionKind.From<TResource>();
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
            try
            {
                _start.Dispose();
                _ready.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // ignore redundant exception to allow shutdown sequence to progress uninterrupted
            }
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public void StartWatching()
    {
        Status = ResourceInformerStatus.Starting;
        _start.Release();
    }

    /// <inheritdoc/>
    public virtual IResourceInformerRegistration Register(ResourceInformerCallback<TResource> callback)
    {
        return new Registration(this, callback);
    }

    /// <inheritdoc/>
    public IResourceInformerRegistration Register(ResourceInformerCallback<IKubernetesObject<V1ObjectMeta>> callback)
    {
        return new Registration(this, (eventType, resource) => callback(eventType, resource));
    }

    /// <inheritdoc/>
    public virtual async Task ReadyAsync(CancellationToken cancellationToken)
    {
        await _ready.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Release is called after each WaitAsync because
        // the semaphore is being used as a manual reset event
        _ready.Release();
    }

    /// <inheritdoc/>
    public override async Task RunAsync(CancellationToken cancellationToken)
    {
        Status = ResourceInformerStatus.Starting;

        try
        {
            // Wait only for the first RunAsync invocation; retries should continue immediately.
            if (Interlocked.Exchange(ref _startWaited, 1) == 0)
            {
                await _start.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await RunListWatchAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = ResourceInformerStatus.Stopped;
            Logger.LogInformation(
                EventId(EventType.WatchingStopped),
                "Stopped watching {ResourceType} resources from API server.",
                typeof(TResource).Name);
            throw;
        }
        catch (Exception error)
        {
            Status = ResourceInformerStatus.Faulted;
            Logger.LogError(
                EventId(EventType.WatchingFailed),
                error,
                "Failed while watching {ResourceType} resources from API server.",
                typeof(TResource).Name);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task RunInfinite(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Status = ResourceInformerStatus.Retrying;

                    Logger.LogWarning(
                        EventId(EventType.WatchingRetryScheduled),
                        "Retrying {ResourceType} in 10s",
                        typeof(TResource).Name);

                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Status = ResourceInformerStatus.Stopped;
        }
    }

    private async Task RunListWatchAsync(CancellationToken cancellationToken)
    {
        var limiter = new Limiter(new Limit(0.2), 3);
        var shouldSync = true;
        var firstSync = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (shouldSync)
                {
                    await ListAsync(cancellationToken).ConfigureAwait(true);
                    shouldSync = false;
                }

                if (firstSync)
                {
                    _ready.Release();
                    firstSync = false;
                }

                Status = ResourceInformerStatus.Running;
                await WatchAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (IOException ex) when (ex.InnerException is SocketException)
            {
                LogWatchError(ex);
            }
            catch (HttpRequestException ex)
            {
                LogWatchError(ex);
            }
            catch (KubernetesException ex)
            {
                LogWatchError(ex);

                // Resource version expiry requires a full re-sync so no updates are missed.
                if (IsExpiredResourceVersion(ex))
                {
                    shouldSync = true;
                }
            }

            // rate limiting the reconnect loop
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private static EventId EventId(EventType eventType) => new EventId((int)eventType, eventType.ToString());

    private void LogWatchError(Exception error)
    {
        Logger.LogDebug(
            EventId(EventType.ReceivedError),
            "Received error watching {ResourceType}: {ErrorMessage}",
            typeof(TResource).Name,
            error);
    }

    private static bool IsExpiredResourceVersion(KubernetesException exception)
        => string.Equals(exception.Status?.Reason, "Expired", StringComparison.Ordinal);

    private Task<HttpOperationResponse<TResponse>> CreateResponseAsync<TResponse>(bool? watch, int? limit, bool? allowWatchBookmarks, string? continueParameter, string? resourceVersion, CancellationToken cancellationToken)
        where TResponse : class
    {
        if (string.IsNullOrEmpty(_namespace))
        {
            return Client.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync<TResponse>(
                _names.Group,
                _names.ApiVersion,
                _names.PluralName,
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
            _names.Group,
            _names.ApiVersion,
            _namespace,
            _names.PluralName,
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
        var previousCache = _cache;
        _cache = [];

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
                limit: 1000,
                allowWatchBookmarks: null,
                continueParameter: continueParameter,
                resourceVersion: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var list = listWithHttpMessage.Body;
            foreach (var item in list.Items)
            {
                // These properties are not already set on items while listing
                // assigned here for consistency
                item.ApiVersion = _names.GroupApiVersion;
                item.Kind = _names.Kind;

                var key = NamespacedName.From(item);
                _cache[key] = item?.Metadata?.OwnerReferences;

                var watchEventType = WatchEventType.Added;
                if (previousCache.Remove(key))
                {
                    // an already-known key is provided as a modification for re-sync purposes
                    watchEventType = WatchEventType.Modified;
                }

                InvokeRegistrationCallbacks(watchEventType, item);
            }

            foreach (var (key, value) in previousCache)
            {
                // for anything which was previously known but not part of list
                // send a deleted notification to clear any observer caches
                var item = new TResource()
                {
                    ApiVersion = _names.GroupApiVersion,
                    Kind = _names.Kind,
                    Metadata = new V1ObjectMeta()
                    {
                        Name = key.Name,
                        NamespaceProperty = key.Namespace,
                        OwnerReferences = value
                    }
                };

                InvokeRegistrationCallbacks(WatchEventType.Deleted, item);
            }

            // keep track of values needed for next page and to start watching
            _lastResourceVersion = list.ResourceVersion();
            continueParameter = list.Continue();
        }
        while (!string.IsNullOrEmpty(continueParameter));

        Logger.LogInformation(
            EventId(EventType.SynchronizeComplete),
            "Completed synchronizing {ResourceType} resources from API server.",
            typeof(TResource).Name);
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
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
            _cache[NamespacedName.From(item)] = item.Metadata?.OwnerReferences;
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

    private void InvokeRegistrationCallbacks(WatchEventType eventType, TResource resource)
    {
        List<Exception>? innerExceptions = default;

        foreach (var registration in _registrations)
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
