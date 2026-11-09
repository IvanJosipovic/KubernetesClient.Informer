// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KubernetesClient.Informer.Client;

/// <summary>
/// Callback for resource event notifications.
/// </summary>
/// <typeparam name="TResource">The type of <see cref="IKubernetesObject{V1ObjectMeta}"/> being monitored.</typeparam>
/// <param name="eventType">The type of change event which was received.</param>
/// <param name="resource">The instance of the resource which was received.</param>
/// <remarks>
/// Callbacks are delivered asynchronously and sequentially for each registration; different registrations may be
/// invoked concurrently. Keep them short and non-blocking. If a callback throws, the error is logged and that
/// registration is removed; the informer and other registrations continue. A deletion caused by relisting supplies
/// the last object observed by this informer, while a watch deletion supplies
/// the object from the watch event. When a callback is registered after the initial list has completed, the current
/// cached resources are queued for it as Added notifications before Register returns.
/// </remarks>
public delegate void ResourceInformerCallback<TResource>(WatchEventType eventType, TResource resource) where TResource : class, IKubernetesObject<V1ObjectMeta>;

/// <summary>
/// Interface IResourceInformer is a service which generates
/// notifications for a specific type
/// of Kubernetes object. The callback eventType informs if the notification
/// is because it is new, modified, or has been deleted.
/// Implements the <see cref="IHostedService" />.
/// </summary>
/// <typeparam name="TResource">The type of the t resource.</typeparam>
/// <seealso cref="IObservable{T}" />
/// <seealso cref="IHostedService" />
public interface IResourceInformer<TResource> : IHostedService, IResourceInformer
    where TResource : class, IKubernetesObject<V1ObjectMeta>, new()
{
    /// <summary>
    /// Registers a callback for change notification. A callback registered before the initial list completes
    /// receives that list. A callback registered after the initial list completes receives the current cached
    /// resources as Added notifications before this method returns, then receives subsequent changes. These
    /// notifications are delivered asynchronously. The returned registration should be disposed when the receiver
    /// is ending its work.
    /// </summary>
    /// <param name="callback">The delegate that is invoked with each resource notification.</param>
    /// <returns>A registration that should be disposed to end the notifications.</returns>
    IResourceInformerRegistration Register(ResourceInformerCallback<TResource> callback);

    /// <summary>
    /// Runs the informer indefinitely until the provided cancellation token is cancelled. This method is intended to be used instead of host-managed running, such as in a console application or as part of a long-running process; only one run mode may be active at a time. The method will block until cancellation, then stop the informer.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token that can be used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task RunInfinite(CancellationToken cancellationToken);
}

public interface IResourceInformer
{
    /// <summary>
    /// Gets the current lifecycle state of the informer.
    /// </summary>
    ResourceInformerStatus Status { get; }

    /// <summary>
    /// Signals the resource informer to begin listing and watching resources. This is called automatically when the
    /// informer is registered with the host unless host-start watching is disabled for coordinated startup.
    /// </summary>
    void StartWatching();

    /// <summary>
    /// Returns a task that can be awaited to know when the initial listing and its notifications to registrations
    /// present at initial sync are complete. Use the returned registration's ReadyAsync to wait for that specific
    /// registration, including a late registration's cached-state replay. If a callback for a registration throws
    /// during initial delivery, that registration's ReadyAsync faults, but the informer continues. If the informer
    /// terminates before the initial sync, this task faults or is canceled with the run.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>Task.</returns>
    Task ReadyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Registers a callback for change notification. A callback registered before the initial list completes
    /// receives that list. A callback registered after the initial list completes receives the current cached
    /// resources as Added notifications before this method returns, then receives subsequent changes. These
    /// notifications are delivered asynchronously. The returned registration should be disposed when the receiver
    /// is ending its work.
    /// </summary>
    /// <param name="callback">The delegate that is invoked with each resource notification.</param>
    /// <returns>A registration that should be disposed to end the notifications.</returns>
    IResourceInformerRegistration Register(ResourceInformerCallback<IKubernetesObject<V1ObjectMeta>> callback);
}
