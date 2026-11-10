// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KubernetesClient.Informer.Tests.TestCluster.Models;
using KubernetesClient.Informer.Tests.Utils;

namespace KubernetesClient.Informer.Tests.TestCluster;

public class TestCluster : ITestCluster
{
    public IList<ResourceObject> Resources { get; } = new List<ResourceObject>();

    private readonly TaskCompletionSource<ListParameters> _listRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ListParameters> _watchRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _watchListRequestSync = new();
    private readonly Dictionary<int, TaskCompletionSource<ListParameters>> _watchListRequestWaiters = new();
    private readonly TaskCompletionSource<ListParameters> _watchListRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _watchListInitialEventsSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _watchRequestSync = new();
    private readonly Dictionary<int, TaskCompletionSource<ListParameters>> _watchRequestWaiters = new();
    private readonly object _listResponseSync = new();
    private readonly Queue<ListResult> _listResponses = new();

    public ListParameters? LastListParameters { get; private set; }

    public IList<ListParameters> ListRequests { get; } = new List<ListParameters>();

    public IList<ListParameters> WatchRequests { get; } = new List<ListParameters>();

    public IList<ListParameters> WatchListRequests { get; } = new List<ListParameters>();

    public bool WatchListSupported { get; set; }

    public bool WatchListEndsBeforeInitialEventsEnd { get; set; }

    public Task WatchListInitialEventsSent => _watchListInitialEventsSent.Task;

    public Task? WatchListResponseGate { get; set; }

    public Task<ListParameters> ListRequested => _listRequested.Task;

    public Task<ListParameters> WatchRequested => _watchRequested.Task;

    public Task<ListParameters> WatchListRequested => _watchListRequested.Task;

    public Task<ListParameters> WaitForWatchRequestAsync(int requestNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestNumber);

        lock (_watchRequestSync)
        {
            if (WatchRequests.Count >= requestNumber)
            {
                return Task.FromResult(WatchRequests[requestNumber - 1]);
            }

            if (!_watchRequestWaiters.TryGetValue(requestNumber, out var waiter))
            {
                waiter = new TaskCompletionSource<ListParameters>(TaskCreationOptions.RunContinuationsAsynchronously);
                _watchRequestWaiters.Add(requestNumber, waiter);
            }

            return waiter.Task;
        }
    }

    public Task<ListParameters> WaitForWatchListRequestAsync(int requestNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestNumber);

        lock (_watchListRequestSync)
        {
            if (WatchListRequests.Count >= requestNumber)
            {
                return Task.FromResult(WatchListRequests[requestNumber - 1]);
            }

            if (!_watchListRequestWaiters.TryGetValue(requestNumber, out var waiter))
            {
                waiter = new TaskCompletionSource<ListParameters>(TaskCreationOptions.RunContinuationsAsynchronously);
                _watchListRequestWaiters.Add(requestNumber, waiter);
            }

            return waiter.Task;
        }
    }

    public void MarkWatchListInitialEventsSent()
        => _watchListInitialEventsSent.TrySetResult();

    public IList<WatchEvent> WatchEvents { get; } = new List<WatchEvent>();

    public Task? WatchResponseGate { get; set; }

    public TestCluster(IOptions<TestClusterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var resource in options.Value.InitialResources)
        {
            Resources.Add(ResourceSerializers.Convert<ResourceObject>(resource));
        }
    }

    public void EnqueueListResponse(ListResult response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_listResponseSync)
        {
            _listResponses.Enqueue(response);
        }
    }

    public virtual Task UnhandledRequest(HttpContext context)
    {
        throw new NotImplementedException();
    }

    public virtual Task<ListResult> ListResourcesAsync(string group, string version, string plural, ListParameters parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(plural);
        ArgumentNullException.ThrowIfNull(parameters);

        LastListParameters = parameters;
        if (parameters.Watch == true)
        {
            if (parameters.SendInitialEvents == true)
            {
                lock (_watchListRequestSync)
                {
                    WatchListRequests.Add(parameters);
                    _watchListRequested.TrySetResult(parameters);
                    if (_watchListRequestWaiters.Remove(WatchListRequests.Count, out var waiter))
                    {
                        waiter.TrySetResult(parameters);
                    }
                }
            }
            else
            {
                lock (_watchRequestSync)
                {
                    WatchRequests.Add(parameters);
                    _watchRequested.TrySetResult(parameters);
                    if (_watchRequestWaiters.Remove(WatchRequests.Count, out var waiter))
                    {
                        waiter.TrySetResult(parameters);
                    }
                }
            }
        }
        else
        {
            ListRequests.Add(parameters);
            _listRequested.TrySetResult(parameters);
        }

        if (parameters.Watch != true)
        {
            lock (_listResponseSync)
            {
                if (_listResponses.TryDequeue(out var response))
                {
                    return Task.FromResult(response);
                }
            }
        }

        return Task.FromResult(new ListResult
        {
            ResourceVersion = parameters.ResourceVersion,
            Continue = null,
            Items = Resources.ToArray(),
        });
    }
}
