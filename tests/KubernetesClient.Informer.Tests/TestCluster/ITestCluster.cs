// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using KubernetesClient.Informer.Tests.TestCluster.Models;

namespace KubernetesClient.Informer.Tests.TestCluster;

public interface ITestCluster
{
    Task UnhandledRequest(HttpContext context);

    Task<ListResult> ListResourcesAsync(string group, string version, string plural, ListParameters parameters);

    void EnqueueListResponse(ListResult response);

    ListParameters? LastListParameters { get; }

    IList<ListParameters> ListRequests { get; }

    IList<ListParameters> WatchRequests { get; }

    IList<ListParameters> WatchListRequests { get; }

    bool WatchListSupported { get; set; }

    bool WatchListEndsBeforeInitialEventsEnd { get; set; }

    Task WatchListInitialEventsSent { get; }

    Task? WatchListResponseGate { get; set; }

    void MarkWatchListInitialEventsSent();

    Task<ListParameters> ListRequested { get; }

    Task<ListParameters> WatchRequested { get; }

    Task<ListParameters> WaitForWatchRequestAsync(int requestNumber);

    Task<ListParameters> WaitForWatchListRequestAsync(int requestNumber);

    IList<WatchEvent> WatchEvents { get; }

    Task? WatchResponseGate { get; set; }

    /// <summary>
    /// When set, the watch controller writes one event, flushes it, signals
    /// <see cref="WatchStreamFirstEventSent"/>, then waits for this gate before
    /// either aborting the connection or completing the response.
    /// </summary>
    Task? WatchStreamGate { get; set; }

    Task WatchStreamFirstEventSent { get; }

    void MarkWatchStreamFirstEventSent();

    /// <summary>Abort the connection after <see cref="WatchStreamGate"/> is released.</summary>
    bool AbortWatchStream { get; set; }

    Task WatchStreamAborted { get; }

    void MarkWatchStreamAborted();

    /// <summary>
    /// Intercepts a client request before it reaches the HTTP server. Return a response to
    /// serve it from the test, or <see langword="null"/> to fall through to the server.
    /// Throwing an exception surfaces it directly to the informer transport.
    /// </summary>
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? RequestHandler { get; set; }
}
