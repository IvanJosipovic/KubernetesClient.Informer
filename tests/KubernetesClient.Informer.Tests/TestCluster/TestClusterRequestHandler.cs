// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;

namespace KubernetesClient.Informer.Tests.TestCluster;

/// <summary>
/// Client-side handler that lets a test serve or fail an HTTP request directly,
/// bypassing the in-process server when a <see cref="ITestCluster.RequestHandler"/> is set.
/// </summary>
public sealed class TestClusterRequestHandler : DelegatingHandler
{
    public TestClusterRequestHandler()
        : base(new HttpClientHandler())
    {
    }

    public ITestCluster? Cluster { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Cluster?.RequestHandler is { } handler
            && await handler(request, cancellationToken).ConfigureAwait(false) is { } response)
        {
            return response;
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
