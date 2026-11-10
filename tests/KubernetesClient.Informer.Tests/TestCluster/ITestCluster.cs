// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http;
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
}
