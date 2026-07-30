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

    public ListParameters? LastListParameters { get; private set; }

    public IList<ListParameters> ListRequests { get; } = new List<ListParameters>();

    public IList<ListParameters> WatchRequests { get; } = new List<ListParameters>();

    public Task<ListParameters> ListRequested => _listRequested.Task;

    public Task<ListParameters> WatchRequested => _watchRequested.Task;

    public IList<WatchEvent> WatchEvents { get; } = new List<WatchEvent>();

    public TestCluster(IOptions<TestClusterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var resource in options.Value.InitialResources)
        {
            Resources.Add(ResourceSerializers.Convert<ResourceObject>(resource));
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
            WatchRequests.Add(parameters);
            _watchRequested.TrySetResult(parameters);
        }
        else
        {
            ListRequests.Add(parameters);
            _listRequested.TrySetResult(parameters);
        }

        return Task.FromResult(new ListResult
        {
            ResourceVersion = parameters.ResourceVersion,
            Continue = null,
            Items = Resources.ToArray(),
        });
    }
}
