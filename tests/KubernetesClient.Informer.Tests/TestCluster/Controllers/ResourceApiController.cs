// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Collections.Generic;
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
        if (parameters.Watch == true && parameters.SendInitialEvents == true)
        {
            var initialList = await _testCluster.ListResourcesAsync(string.Empty, Version, Plural, parameters);
            if (!_testCluster.WatchListSupported)
            {
                return StatusCode(StatusCodes.Status422UnprocessableEntity, new
                {
                    kind = "Status",
                    apiVersion = "v1",
                    status = "Failure",
                    message = "sendInitialEvents is forbidden for watch unless the WatchList feature gate is enabled",
                    reason = "Invalid",
                    code = StatusCodes.Status422UnprocessableEntity,
                });
            }

            Response.ContentType = "application/json";
            foreach (var item in initialList.Items)
            {
                await Response.WriteAsync(JsonSerializer.Serialize(new WatchEvent { Type = "ADDED", Object = item }));
                await Response.WriteAsync("\n");
            }

            await Response.Body.FlushAsync(HttpContext.RequestAborted);
            _testCluster.MarkWatchListInitialEventsSent();
            if (_testCluster.WatchListResponseGate is { } watchListResponseGate)
            {
                await watchListResponseGate.WaitAsync(HttpContext.RequestAborted);
            }

            if (_testCluster.WatchListEndsBeforeInitialEventsEnd)
            {
                return new EmptyResult();
            }

            var bookmark = new ResourceObject
            {
                ApiVersion = Version,
                Kind = initialList.Items.FirstOrDefault()?.Kind ?? string.Empty,
                Metadata = new k8s.Models.V1ObjectMeta
                {
                    ResourceVersion = initialList.ResourceVersion ?? "1",
                    Annotations = new Dictionary<string, string> { ["k8s.io/initial-events-end"] = "true" },
                },
            };
            await Response.WriteAsync(JsonSerializer.Serialize(new WatchEvent { Type = "BOOKMARK", Object = bookmark }));
            await Response.WriteAsync("\n");
            return new EmptyResult();
        }

        if (parameters.Watch == true)
        {
            await _testCluster.ListResourcesAsync(string.Empty, Version, Plural, parameters);
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
