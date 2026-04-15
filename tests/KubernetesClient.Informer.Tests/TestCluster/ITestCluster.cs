// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using KubernetesClient.Informer.Tests.TestCluster.Models;

namespace KubernetesClient.Informer.Tests.TestCluster;

public interface ITestCluster
{
    Task UnhandledRequest(HttpContext context);

    Task<ListResult> ListResourcesAsync(string group, string version, string plural, ListParameters parameters);
}
