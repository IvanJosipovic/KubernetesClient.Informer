// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Threading.Tasks;
using KubernetesClient.Informer.Tests.TestCluster.Models;

namespace KubernetesClient.Informer.Tests.TestCluster;

[Route("api/{version}/{plural}")]
public class ResourceApiController : ControllerBase
{
    private readonly ITestCluster _testCluster;

    public ResourceApiController(ITestCluster testCluster)
    {
        _testCluster = testCluster;
    }

    [FromRoute]
    public string Version { get; set; }

    [FromRoute]
    public string Plural { get; set; }

    [HttpGet]
    public async Task<IActionResult> ListAsync(ListParameters parameters)
    {
        if (parameters.Watch == true)
        {
            await _testCluster.ListResourcesAsync(string.Empty, Version, Plural, parameters);
            Response.ContentType = "application/json";
            foreach (var watchEvent in _testCluster.WatchEvents)
            {
                await Response.WriteAsync(JsonSerializer.Serialize(watchEvent));
                await Response.WriteAsync("\n");
            }

            return new EmptyResult();
        }

        var list = await _testCluster.ListResourcesAsync(string.Empty, Version, Plural, parameters);

        var result = new KubernetesList<ResourceObject>(list.Items, Version, V1PodList.KubeKind, new V1ListMeta()
        {
            ContinueProperty = list.Continue,
            RemainingItemCount = null,
            ResourceVersion = list.ResourceVersion
        });

        return new ObjectResult(result);
    }
}
