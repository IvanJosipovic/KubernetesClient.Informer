// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s.Models;
using System;
using System.Reflection;

namespace KubernetesClient.Informer.Client;

/// <summary>
/// Describes the API group, version, kind, and plural resource name for a Kubernetes resource.
/// </summary>
public struct GroupApiVersionKind : IEquatable<GroupApiVersionKind>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GroupApiVersionKind"/> struct.
    /// </summary>
    /// <param name="group">The API group.</param>
    /// <param name="apiVersion">The API version.</param>
    /// <param name="kind">The resource kind.</param>
    /// <param name="pluralName">The plural resource name.</param>
    public GroupApiVersionKind(string group, string apiVersion, string kind, string pluralName)
    {
        Group = group;
        ApiVersion = apiVersion;
        GroupApiVersion = string.IsNullOrEmpty(group) ? apiVersion : $"{group}/{apiVersion}";
        Kind = kind;
        PluralName = pluralName;
    }

    /// <summary>Gets the API group.</summary>
    public string Group { get; }

    /// <summary>Gets the API version.</summary>
    public string ApiVersion { get; }

    /// <summary>Gets the group-qualified API version.</summary>
    public string GroupApiVersion { get; }

    /// <summary>Gets the resource kind.</summary>
    public string Kind { get; }

    /// <summary>Gets or sets the plural resource name.</summary>
    public string PluralName { get; set; }

    /// <summary>Creates API metadata from the Kubernetes entity attribute on a resource type.</summary>
    /// <typeparam name="TResource">The Kubernetes resource type.</typeparam>
    /// <returns>The API metadata declared by the resource type.</returns>
    public static GroupApiVersionKind From<TResource>() => From(typeof(TResource));

    /// <summary>Creates API metadata from a type's Kubernetes entity attribute.</summary>
    /// <param name="resourceType">The Kubernetes resource type.</param>
    /// <returns>The API metadata declared by the resource type.</returns>
    /// <exception cref="ArgumentException">The type has no Kubernetes entity attribute.</exception>
    public static GroupApiVersionKind From(Type resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);

        var entity = resourceType.GetTypeInfo().GetCustomAttribute<KubernetesEntityAttribute>()
            ?? throw new ArgumentException($"Type '{resourceType}' is not a Kubernetes entity.", nameof(resourceType));

        return new GroupApiVersionKind(
            group: entity.Group,
            apiVersion: entity.ApiVersion,
            kind: entity.Kind,
            pluralName: entity.PluralName);
    }

    /// <summary>Determines whether this instance is equal to the specified object.</summary>
    /// <param name="obj">The object to compare with this instance.</param>
    /// <returns><see langword="true"/> if the object is an equal <see cref="GroupApiVersionKind"/>; otherwise, <see langword="false"/>.</returns>
    public override bool Equals(object? obj)
    {
        return obj is GroupApiVersionKind kind && Equals(kind);
    }

    /// <summary>Determines whether this instance is equal to another API metadata value.</summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns><see langword="true"/> if the values are equal; otherwise, <see langword="false"/>.</returns>
    public bool Equals(GroupApiVersionKind other)
    {
        return GroupApiVersion == other.GroupApiVersion
            && Kind == other.Kind;
    }

    /// <summary>Returns the hash code for this instance.</summary>
    /// <returns>A hash code based on the group-qualified API version and kind.</returns>
    public override int GetHashCode()
    {
        return HashCode.Combine(GroupApiVersion, Kind);
    }

    /// <summary>Returns the string representation of this API metadata.</summary>
    /// <returns>The kind and group-qualified API version.</returns>
    public override string ToString()
    {
        return $"{Kind}.{GroupApiVersion}";
    }

    /// <summary>Determines whether two API metadata values are equal.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> if the values are equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator ==(GroupApiVersionKind left, GroupApiVersionKind right)
    {
        return left.Equals(right);
    }

    /// <summary>Determines whether two API metadata values are not equal.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> if the values are not equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator !=(GroupApiVersionKind left, GroupApiVersionKind right)
    {
        return !(left == right);
    }
}
