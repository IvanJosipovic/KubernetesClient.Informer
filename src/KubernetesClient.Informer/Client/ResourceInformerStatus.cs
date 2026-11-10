// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace KubernetesClient.Informer.Client;

/// <summary>Describes the current lifecycle state of a resource informer.</summary>
public enum ResourceInformerStatus
{
    /// <summary>The informer has not started.</summary>
    NotStarted = 0,
    /// <summary>The informer is starting or synchronizing.</summary>
    Starting = 1,
    /// <summary>The informer is watching resources.</summary>
    Running = 2,
    /// <summary>The informer is waiting before retrying after a failure.</summary>
    Retrying = 3,
    /// <summary>The informer has stopped.</summary>
    Stopped = 4,
    /// <summary>The informer has terminated because of a failure.</summary>
    Faulted = 5,
}
