// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace KubernetesClient.Informer.Client;

/// <summary>
/// OpenTelemetry tracing configuration for resource informers.
/// </summary>
public static class InformerDiagnostics
{
    /// <summary>
    /// The name of the <see cref="ActivitySource"/> used by resource informers.
    /// </summary>
    public const string ActivitySourceName = "KubernetesClient.Informer";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
