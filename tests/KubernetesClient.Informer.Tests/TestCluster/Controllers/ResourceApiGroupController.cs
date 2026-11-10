// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KubernetesClient.Informer.Tests.TestCluster.Models;

namespace KubernetesClient.Informer.Tests.TestCluster;

[Route("apis/{group}/{version}/{plural}")]
public class ResourceApiGroupController : ControllerBase
{
    private readonly ITestCluster _testCluster;

    public ResourceApiGroupController(ITestCluster testCluster)
    {
        _testCluster = testCluster;
    }

    [FromRoute]
    public string Group { get; set; }

    [FromRoute]
    public string Version { get; set; }

    [FromRoute]
    public string Plural { get; set; }

    [HttpGet]
    public async Task<IActionResult> ListAsync(ListParameters parameters)
    {
        if (parameters.Watch == true)
        {
            await _testCluster.ListResourcesAsync(Group, Version, Plural, parameters);
            if (_testCluster.WatchResponseGate is { } watchResponseGate)
            {
                await watchResponseGate.WaitAsync(HttpContext.RequestAborted);
            }

            Response.ContentType = "application/json";
            foreach (var watchEvent in _testCluster.WatchEvents.ToArray())
            {
                await Response.WriteAsync(JsonSerializer.Serialize(watchEvent));
                await Response.WriteAsync("\n");
                _testCluster.WatchEvents.Remove(watchEvent);
            }

            return new EmptyResult();
        }

        var list = await _testCluster.ListResourcesAsync(Group, Version, Plural, parameters);

        var result = new KubernetesList<ResourceObject>(list.Items, Version, V1DeploymentList.KubeKind, new V1ListMeta()
        {
            ContinueProperty = list.Continue,
            RemainingItemCount = null,
            ResourceVersion = list.ResourceVersion
        });

        return new ObjectResult(result);
    }
}
