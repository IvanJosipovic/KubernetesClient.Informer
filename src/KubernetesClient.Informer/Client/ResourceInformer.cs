// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
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
    private static readonly TimeSpan HealthyWatchDuration = TimeSpan.FromMinutes(1);
    private const int WatchListBootstrapTimeoutSeconds = 30;
    private const string InitialEventsEndAnnotation = "k8s.io/initial-events-end";

    private readonly object _sync = new();
    private readonly GroupApiVersionKind _groupApiVersionKind;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ResourceSelector<TResource>? _selector;
    private ImmutableList<Registration> _registrations = [];
    private Dictionary<NamespacedName, TResource> _cache = [];
    private string? _lastResourceVersion;
    private readonly string? _namespace;
    private readonly TimeProvider _timeProvider;
    private int _status = (int)ResourceInformerStatus.NotStarted;
    private int? _timeoutSeconds;
    private readonly int _resourceListLimit;
    private bool _disposed;
    private bool _initialListCompleted;
    private bool _lastResourceVersionUnavailable;
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
    /// <param name="timeProvider">The time provider used for retry delays and watch timeouts.</param>
    public ResourceInformer(
        IKubernetes client,
        IHostApplicationLifetime hostApplicationLifetime,
        ILogger<ResourceInformer<TResource>> logger,
        ResourceSelector<TResource>? selector = null,
        string? @namespace = null,
        int resourceListLimit = 1000,
        int? timeoutSeconds = null,
        GroupApiVersionKind? groupApiVersionKind = null,
        TimeProvider? timeProvider = null)
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
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        CallbackFailed = 111,
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
                    foreach (var registration in _registrations)
                    {
                        registration.Complete();
                    }

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

        Registration registration;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            registration = new Registration(this, callback, waitForInformerSync: !_initialListCompleted);
            _registrations = _registrations.Add(registration);

            if (_initialListCompleted)
            {
                foreach (var resource in _cache.Values)
                {
                    registration.Enqueue(WatchEventType.Added, resource);
                }

                registration.EnqueueReady();
            }

            registration.Start();
        }

        return registration;
    }

    /// <inheritdoc/>
    public IResourceInformerRegistration Register(ResourceInformerCallback<IKubernetesObject<V1ObjectMeta>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return Register((ResourceInformerCallback<TResource>)((eventType, resource) => callback(eventType, resource)));
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
                if (!isRunInfiniteInvocation || error is OperationCanceledException)
                {
                    Logger.LogError(
                        EventId(EventType.WatchingFailed),
                        error,
                        "Failed while watching {ResourceType} resources from API server.",
                        typeof(TResource).Name);
                }
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
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not OperationCanceledException)
                {
                    Status = ResourceInformerStatus.Retrying;
                    var retryDelay = GetRetryDelay(error, retryAttempt++, _timeProvider);

                    Logger.Log(
                        GetFailureLogLevel(error, GetStatusCode(error)),
                        EventId(EventType.WatchingRetryScheduled),
                        error,
                        "Retrying {ResourceType} in {RetryDelay} after a failure.",
                        typeof(TResource).Name,
                        retryDelay);

                    await Task.Delay(retryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
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
        var limiter = new Limiter(new Limit(0.2), 3, _timeProvider);
        var shouldSync = true;
        var firstSync = true;
        var watchListSupported = true;
        var retryAttempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (shouldSync)
                {
                    if (watchListSupported)
                    {
                        watchListSupported = await TryWatchListAsync(() =>
                        {
                            shouldSync = false;
                            firstSync = false;
                            _ready.TrySetResult();
                        }, cancellationToken).ConfigureAwait(true);
                    }

                    if (shouldSync)
                    {
                        await ListAsync(cancellationToken).ConfigureAwait(true);
                        shouldSync = false;
                    }
                }

                if (firstSync)
                {
                    _ready.TrySetResult();
                    firstSync = false;
                }

                Status = ResourceInformerStatus.Running;
                var watchStartedAt = _timeProvider.GetTimestamp();
                try
                {
                    await WatchAsync(cancellationToken).ConfigureAwait(true);
                }
                catch
                {
                    if (_timeProvider.GetElapsedTime(watchStartedAt) >= HealthyWatchDuration)
                    {
                        retryAttempt = 0;
                    }

                    throw;
                }

                Status = ResourceInformerStatus.Retrying;
                retryAttempt = 0;
            }
            catch (IOException ex) when (ex.InnerException is SocketException)
            {
                Status = ResourceInformerStatus.Retrying;
                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Status = ResourceInformerStatus.Retrying;
                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                Status = ResourceInformerStatus.Retrying;
                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpOperationException ex)
            {
                Status = ResourceInformerStatus.Retrying;

                if (IsExpiredResourceVersion(ex))
                {
                    shouldSync = true;
                }

                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (KubernetesException ex)
            {
                Status = ResourceInformerStatus.Retrying;

                // Resource version expiry requires a full re-sync so no updates are missed.
                if (IsExpiredResourceVersion(ex))
                {
                    shouldSync = true;
                }

                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Status = ResourceInformerStatus.Retrying;
                await WaitBeforeRetryAsync(ex, retryAttempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // rate limiting the reconnect loop
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private static EventId EventId(EventType eventType) => new EventId((int)eventType, eventType.ToString());

    private async Task<bool> TryWatchListAsync(Action initialSyncCompleted, CancellationToken cancellationToken)
    {
        using var activity = InformerDiagnostics.ActivitySource.StartActivity(nameof(TryWatchListAsync), ActivityKind.Client);
        SetResourceTags(activity);
        using var bootstrapTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bootstrapTimeoutSeconds = _timeoutSeconds is > 0
            ? _timeoutSeconds.Value
            : WatchListBootstrapTimeoutSeconds;
        bootstrapTimeout.CancelAfter(TimeSpan.FromSeconds(bootstrapTimeoutSeconds));

        try
        {
            return await TryWatchListCoreAsync(initialSyncCompleted, activity, bootstrapTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "WatchList bootstrap timed out.");
            return false;
        }
    }

    private async Task<bool> TryWatchListCoreAsync(
        Action initialSyncCompleted,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        HttpOperationResponse<TResource>? watchListResponse;
        try
        {
            watchListResponse = await CreateWatchListResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WatchListUnsupportedException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "The API server rejected sendInitialEvents.");
            return false;
        }

        if (watchListResponse is null)
        {
            return false;
        }

        using (watchListResponse)
        using (watchListResponse.Request)
        {
            var initialItems = new List<TResource>();
            var nextCache = new Dictionary<NamespacedName, TResource>();

            await foreach (var (eventType, resource) in ReadWatchEventsAsync(
                watchListResponse.Response.Content,
                cancellationToken).ConfigureAwait(false))
            {
                if (eventType == WatchEventType.Bookmark)
                {
                    if (!IsInitialEventsEnd(resource))
                    {
                        continue;
                    }

                    var resourceVersion = resource.ResourceVersion();
                    if (string.IsNullOrEmpty(resourceVersion))
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, "The initial-events bookmark did not include a resource version.");
                        return false;
                    }

                    await CommitInitialCacheAsync(nextCache, initialItems, resourceVersion, activity).ConfigureAwait(false);
                    initialSyncCompleted();
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return true;
                }

                // The initial-events phase is a consistent snapshot of ADDED events. Never
                // publish any part of it until the API server's completion bookmark arrives.
                if (eventType != WatchEventType.Added)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Unexpected event before the initial-events bookmark.");
                    return false;
                }

                resource.ApiVersion = _groupApiVersionKind.GroupApiVersion;
                resource.Kind = _groupApiVersionKind.Kind;
                initialItems.Add(resource);
                nextCache[NamespacedName.From(resource)] = resource;
            }
        }

        // Older API servers can ignore unknown query parameters and end the watch without
        // the marker. Fall back instead of publishing an incomplete cache.
        activity?.SetStatus(ActivityStatusCode.Error, "The watch ended before the initial-events bookmark.");
        return false;
    }

    private static bool IsInitialEventsEnd(TResource resource)
        => resource.Metadata?.Annotations?.TryGetValue(InitialEventsEndAnnotation, out var value) == true
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static async IAsyncEnumerable<(WatchEventType EventType, TResource Resource)> ReadWatchEventsAsync(
        HttpContent content,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var genericEvent = KubernetesJson.Deserialize<Watcher<KubernetesObject>.WatchEvent>(line);
            if (genericEvent.Object?.Kind == "Status")
            {
                var statusEvent = KubernetesJson.Deserialize<Watcher<V1Status>.WatchEvent>(line);
                throw new KubernetesException(statusEvent.Object);
            }

            var watchEvent = KubernetesJson.Deserialize<Watcher<TResource>.WatchEvent>(line);
            yield return (watchEvent.Type, watchEvent.Object);
        }
    }

    private void LogListWatchFailure(Exception error, TimeSpan retryDelay)
    {
        InformerDiagnostics.RecordException(Activity.Current, error);

        var eventId = EventId(EventType.ReceivedError);
        if (IsExpiredResourceVersion(error))
        {
            Logger.LogInformation(
                eventId,
                error,
                "Resource version expired for {ResourceType}; retrying with a full relist in {RetryDelay}.",
                typeof(TResource).Name,
                retryDelay);
            return;
        }

        var statusCode = GetStatusCode(error);
        var level = GetFailureLogLevel(error, statusCode);
        Logger.Log(
            level,
            eventId,
            error,
            "List/watch for {ResourceType} failed with status code {StatusCode}; retrying in {RetryDelay}.",
            typeof(TResource).Name,
            statusCode,
            retryDelay);
    }

    private static LogLevel GetFailureLogLevel(Exception error, int? statusCode)
    {
        if (statusCode is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests
            || statusCode >= 500)
        {
            return LogLevel.Warning;
        }

        if (statusCode is >= 400 and < 500)
        {
            return LogLevel.Error;
        }

        return error is IOException or HttpRequestException or OperationCanceledException
            ? LogLevel.Warning
            : LogLevel.Error;
    }

    private static int? GetStatusCode(Exception error)
        => error switch
        {
            HttpOperationException httpException => (int?)httpException.Response?.StatusCode,
            KubernetesException kubernetesException => kubernetesException.Status?.Code,
            HttpRequestException httpRequestException => (int?)httpRequestException.StatusCode,
            _ => null,
        };

    private static bool IsExpiredResourceVersion(Exception exception)
        => exception switch
        {
            HttpOperationException httpException => IsExpiredResourceVersion(httpException),
            KubernetesException kubernetesException => IsExpiredResourceVersion(kubernetesException),
            _ => false,
        };

    private static bool IsExpiredResourceVersion(KubernetesException exception)
        => exception.Status?.Code == (int)HttpStatusCode.Gone
            || string.Equals(exception.Status?.Reason, "Expired", StringComparison.Ordinal);

    private static bool IsExpiredResourceVersion(HttpOperationException exception)
        => exception.Response?.StatusCode == HttpStatusCode.Gone;

    private string? GetListResourceVersion()
    {
        lock (_sync)
        {
            if (_lastResourceVersionUnavailable)
            {
                return null;
            }

            // Match client-go: start at the watch cache, then relist from the last observed version.
            return string.IsNullOrEmpty(_lastResourceVersion) ? "0" : _lastResourceVersion;
        }
    }

    private void MarkListResourceVersionUnavailable()
    {
        lock (_sync)
        {
            _lastResourceVersionUnavailable = true;
        }
    }

    private static TimeSpan GetRetryDelay(Exception exception, int attempt, TimeProvider timeProvider)
    {
        const double initialSeconds = 1;
        const double maximumBackoffSeconds = 60;
        const double maximumRetryAfterSeconds = 300;

        var backoffSeconds = Math.Min(initialSeconds * Math.Pow(2, Math.Min(attempt, 6)), maximumBackoffSeconds);
        var jitteredBackoff = TimeSpan.FromSeconds(backoffSeconds * (0.5 + Random.Shared.NextDouble()));
        var retryAfter = GetRetryAfter(exception, timeProvider);

        if (retryAfter is null || retryAfter <= jitteredBackoff)
        {
            return jitteredBackoff;
        }

        return retryAfter.Value > TimeSpan.FromSeconds(maximumRetryAfterSeconds)
            ? TimeSpan.FromSeconds(maximumRetryAfterSeconds)
            : retryAfter.Value;
    }

    private static TimeSpan? GetRetryAfter(Exception exception, TimeProvider timeProvider)
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
            var delay = retryAt - timeProvider.GetUtcNow();
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private async Task WaitBeforeRetryAsync(Exception exception, int attempt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retryDelay = GetRetryDelay(exception, attempt, _timeProvider);
        LogListWatchFailure(exception, retryDelay);
        await Task.Delay(retryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

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

    private async Task<HttpOperationResponse<TResource>?> CreateWatchListResponseAsync(CancellationToken cancellationToken)
    {
        // KubernetesClient's generic custom-object methods don't expose sendInitialEvents.
        // Use its configured transport and credentials for this one request; other IKubernetes
        // implementations retain the portable list-then-watch behavior.
        if (Client is not Kubernetes kubernetes)
        {
            return null;
        }

        var path = new StringBuilder();
        if (string.IsNullOrEmpty(_groupApiVersionKind.Group))
        {
            path.Append("api/");
        }
        else
        {
            path.Append("apis/")
                .Append(Uri.EscapeDataString(_groupApiVersionKind.Group))
                .Append('/');
        }

        path.Append(Uri.EscapeDataString(_groupApiVersionKind.ApiVersion));
        if (!string.IsNullOrEmpty(_namespace))
        {
            path.Append("/namespaces/").Append(Uri.EscapeDataString(_namespace));
        }

        path.Append('/').Append(Uri.EscapeDataString(_groupApiVersionKind.PluralName));

        var query = new List<string>
        {
            "watch=true",
            "sendInitialEvents=true",
            "allowWatchBookmarks=true",
            "resourceVersionMatch=NotOlderThan",
            $"timeoutSeconds={(_timeoutSeconds is > 0 ? _timeoutSeconds.Value : WatchListBootstrapTimeoutSeconds)}",
        };

        if (_selector?.FieldSelector is { } fieldSelector)
        {
            query.Add($"fieldSelector={Uri.EscapeDataString(fieldSelector)}");
        }

        var baseUri = Client.BaseUri.ToString().TrimEnd('/') + "/";
        var requestUri = new Uri(new Uri(baseUri, UriKind.Absolute), $"{path}?{string.Join('&', query)}");
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage? response = null;

        try
        {
            await kubernetes.Credentials.ProcessHttpRequestAsync(request, cancellationToken).ConfigureAwait(false);
            response = await kubernetes.HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                    && responseBody.Contains("sendInitialEvents", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WatchListUnsupportedException();
                }

                throw new HttpRequestException(
                    $"WatchList request failed with status {(int)response.StatusCode} ({response.StatusCode}): {responseBody}",
                    inner: null,
                    response.StatusCode);
            }

            return new HttpOperationResponse<TResource>
            {
                Request = request,
                Response = response,
            };
        }
        catch
        {
            response?.Dispose();
            request.Dispose();
            throw;
        }
    }

    private sealed class WatchListUnsupportedException : Exception
    {
    }

    private async Task ListAsync(CancellationToken cancellationToken)
    {
        using var activity = InformerDiagnostics.ActivitySource.StartActivity(nameof(ListAsync), ActivityKind.Client);
        SetResourceTags(activity);
        activity?.SetTag("k8s.informer.resource_version", _lastResourceVersion);

        var nextCache = new Dictionary<NamespacedName, TResource>();
        var listedItems = new List<TResource>();
        var listResourceVersion = GetListResourceVersion();
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
            using var listWithHttpMessage = await CreateListResponseAsync(
                continueParameter,
                listResourceVersion,
                cancellationToken).ConfigureAwait(false);

            var list = listWithHttpMessage.Body;
            foreach (var item in list.Items)
            {
                // These properties are not already set on items while listing
                // assigned here for consistency
                item.ApiVersion = _groupApiVersionKind.GroupApiVersion;
                item.Kind = _groupApiVersionKind.Kind;

                var key = NamespacedName.From(item);
                nextCache[key] = item;
                listedItems.Add(item);
            }

            // keep track of values needed for next page and to start watching
            nextResourceVersion = list.ResourceVersion();
            continueParameter = list.Continue();
        }
        while (!string.IsNullOrEmpty(continueParameter));

        await CommitInitialCacheAsync(nextCache, listedItems, nextResourceVersion, activity).ConfigureAwait(false);

        Logger.LogInformation(
            EventId(EventType.SynchronizeComplete),
            "Completed synchronizing {ResourceType} resources from API server.",
            typeof(TResource).Name);
        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("k8s.informer.resource_count", nextCache.Count);
    }

    private async Task CommitInitialCacheAsync(
        Dictionary<NamespacedName, TResource> nextCache,
        IReadOnlyList<TResource> initialItems,
        string? resourceVersion,
        Activity? activity)
    {
        ImmutableList<Registration> registrations;
        lock (_sync)
        {
            var previousCache = _cache;
            _cache = nextCache;
            _lastResourceVersion = resourceVersion;
            _lastResourceVersionUnavailable = false;
            _initialListCompleted = true;
            registrations = _registrations;

            foreach (var item in initialItems)
            {
                // Existing cache entries are surfaced as modifications during a resync.
                var eventType = previousCache.ContainsKey(NamespacedName.From(item))
                    ? WatchEventType.Modified
                    : WatchEventType.Added;
                EnqueueRegistrationCallbacks(registrations, eventType, item);
            }

            foreach (var (key, item) in previousCache)
            {
                if (nextCache.ContainsKey(key))
                {
                    continue;
                }

                // Reuse the last-known object so delete handlers retain the resource's full state.
                EnqueueRegistrationCallbacks(registrations, WatchEventType.Deleted, item);
            }

            foreach (var registration in registrations)
            {
                registration.EnqueueReady();
            }
        }

        await Task.WhenAll(registrations.Select(registration => registration.InitialDeliveryTask)).ConfigureAwait(false);
        activity?.SetTag("k8s.informer.resource_count", nextCache.Count);
    }

    private async Task<HttpOperationResponse<KubernetesList<TResource>>> CreateListResponseAsync(
        string? continueParameter,
        string? resourceVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CreateResponseAsync<KubernetesList<TResource>>(
                watch: null,
                limit: _resourceListLimit,
                allowWatchBookmarks: null,
                continueParameter: continueParameter,
                resourceVersion: resourceVersion,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException exception) when (IsExpiredResourceVersion(exception))
        {
            MarkListResourceVersionUnavailable();
            throw;
        }
        catch (KubernetesException exception) when (IsExpiredResourceVersion(exception))
        {
            MarkListResourceVersionUnavailable();
            throw;
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var activity = InformerDiagnostics.ActivitySource.StartActivity(nameof(WatchAsync), ActivityKind.Client);
        SetResourceTags(activity);
        activity?.SetTag("k8s.informer.resource_version", _lastResourceVersion);

        Logger.LogInformation(EventId(EventType.WatchingResource), "Watching {ResourceType} starting from resource version {ResourceVersion}.", typeof(TResource).Name, _lastResourceVersion);

        var lastEventUtc = _timeProvider.GetUtcNow();
        using var reconnectCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var checkLastEventUtcTimer = _timeProvider.CreateTimer(_ =>
        {
            var lastEvent = _timeProvider.GetUtcNow() - lastEventUtc;
            if (lastEvent > TimeSpan.FromMinutes(9.5))
            {
                lastEventUtc = DateTimeOffset.MaxValue;
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
                ExceptionDispatchInfo.Capture(e).Throw();
            }, reconnectCancellationTokenSource.Token))
            {
                lastEventUtc = _timeProvider.GetUtcNow();
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

        ImmutableList<Registration> registrations;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

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

            registrations = _registrations;

            if (watchEventType == WatchEventType.Added ||
                watchEventType == WatchEventType.Modified ||
                watchEventType == WatchEventType.Deleted)
            {
                EnqueueRegistrationCallbacks(registrations, watchEventType, item);
            }
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

    private static void EnqueueRegistrationCallbacks(ImmutableList<Registration> registrations, WatchEventType eventType, TResource resource)
    {
        foreach (var registration in registrations)
        {
            registration.Enqueue(eventType, resource);
        }
    }

    private void ReportCallbackFailure(Exception error)
        => Logger.LogError(
            EventId(EventType.CallbackFailed),
            error,
            "ResourceInformerCallback for {ResourceType} failed; removing its registration.",
            typeof(TResource).Name);
}
