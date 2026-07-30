// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace KubernetesClient.Informer.Client;

public enum ResourceInformerStatus
{
    NotStarted = 0,
    Starting = 1,
    Running = 2,
    Retrying = 3,
    Stopped = 4,
    Faulted = 5,
}
