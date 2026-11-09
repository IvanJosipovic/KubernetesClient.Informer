// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace KubernetesClient.Informer.Client;

/// <summary>
/// Returned by <see cref="IResourceInformer{TResource}.Register(ResourceInformerCallback{TResource})"/> to control the lifetime of an event
/// notification connection. Call <see cref="IDisposable.Dispose()"/> when the lifetime of the notification receiver is ending.
/// </summary>
public interface IResourceInformerRegistration : IDisposable
{
    /// <summary>
    /// Returns a task that completes after this registration has received the initial list, or, for a late
    /// registration, after it has received the current cache as Added notifications. Those notifications are
    /// queued by Register and delivered asynchronously. If a callback throws during initial delivery, this task
    /// faults and the registration is removed; the informer and other registrations continue.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Task.</returns>
    Task ReadyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops this registration from receiving newly enqueued notifications.
    /// Notifications already queued are delivered in order when their callbacks complete normally;
    /// consequently, a queued callback may run after this method returns.
    /// </summary>
    /// <remarks>A callback already in progress is not interrupted.</remarks>
    new void Dispose();
}
