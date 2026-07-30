// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace KubernetesClient.Informer.Tests.TestCluster.Models;

public class WatchEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("object")]
    public ResourceObject Object { get; set; }
}
