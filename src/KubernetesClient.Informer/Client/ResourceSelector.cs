// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s;
using k8s.Models;

namespace KubernetesClient.Informer.Client;

/// <summary>
/// Provides a mechanism for <see cref="IResourceInformer{TResource}"/> to constrain search based on fields in the resource.
/// </summary>
public class ResourceSelector<TResource>
    where TResource : class, IKubernetesObject<V1ObjectMeta>, new()
{
    /// <summary>Initializes a selector with a Kubernetes field-selector expression.</summary>
    /// <param name="fieldSelector">The field-selector expression.</param>
    public ResourceSelector(string fieldSelector)
    {
        FieldSelector = fieldSelector;
    }

    /// <summary>Gets the field-selector expression.</summary>
    public string FieldSelector { get; }
}
