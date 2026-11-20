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

    internal static void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        // Only the exception type is exported. Messages and stack traces can contain raw
        // Kubernetes API response bodies (status objects, admission messages, etc.), which
        // may carry sensitive resource data into distributed traces.
        activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity.AddEvent(new ActivityEvent(
            "exception",
            tags: new ActivityTagsCollection
            {
                ["exception.type"] = exception.GetType().FullName
            }));
    }
}
