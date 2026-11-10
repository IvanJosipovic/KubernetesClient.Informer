// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using k8s;
using k8s.Models;
using System.Threading;
using System.Threading.Channels;

namespace KubernetesClient.Informer.Client;

public partial class ResourceInformer<TResource> where TResource : class, IKubernetesObject<V1ObjectMeta>, new()
{
    internal sealed class Registration : IResourceInformerRegistration
    {
        private readonly Channel<Notification> _notifications = Channel.CreateUnbounded<Notification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _initialDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _waitForInformerSync;
        private int _disposedValue;

        private readonly record struct Notification(WatchEventType? EventType, TResource? Resource, bool IsReadyMarker);

        public Registration(ResourceInformer<TResource> resourceInformer, ResourceInformerCallback<TResource> callback, bool waitForInformerSync)
        {
            ResourceInformer = resourceInformer;
            Callback = callback;
            _waitForInformerSync = waitForInformerSync;
        }

        ~Registration()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }

        public ResourceInformer<TResource> ResourceInformer { get; }
        public ResourceInformerCallback<TResource> Callback { get; }
        internal Task InitialDeliveryTask => _initialDelivery.Task;

        internal void Start()
        {
            _ = Task.Run(ProcessNotificationsAsync);
        }

        internal void Enqueue(WatchEventType eventType, TResource resource)
            => _notifications.Writer.TryWrite(new Notification(eventType, resource, IsReadyMarker: false));

        internal void EnqueueReady()
            => _notifications.Writer.TryWrite(new Notification(EventType: null, Resource: null, IsReadyMarker: true));

        internal void Complete()
        {
            EnqueueReady();
            _notifications.Writer.TryComplete();
        }

        private async Task ProcessNotificationsAsync()
        {
            try
            {
                await foreach (var notification in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (notification.IsReadyMarker)
                    {
                        _ready.TrySetResult();
                        _initialDelivery.TrySetResult();
                        continue;
                    }

                    try
                    {
                        Callback.Invoke(notification.EventType!.Value, notification.Resource!);
                    }
                    catch (Exception error)
                    {
                        // Remove the failing callback before logging so a slow logger cannot leave
                        // the failed registration eligible for additional watch notifications.
                        Dispose();
                        ResourceInformer.ReportCallbackFailure(error);
                        _ready.TrySetException(error);
                        _initialDelivery.TrySetResult();
                        while (_notifications.Reader.TryRead(out _))
                        {
                        }

                        return;
                    }
                }
            }
            catch (Exception error)
            {
                ResourceInformer.ReportCallbackFailure(error);
                _ready.TrySetException(error);
                _initialDelivery.TrySetResult();
                Dispose();
            }
            finally
            {
                _ready.TrySetResult();
                _initialDelivery.TrySetResult();
            }
        }

        public async Task ReadyAsync(CancellationToken cancellationToken)
        {
            if (_waitForInformerSync)
            {
                await ResourceInformer.ReadyAsync(cancellationToken).ConfigureAwait(false);
            }

            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposedValue, 1) == 0)
            {
                lock (ResourceInformer._sync)
                {
                    ResourceInformer._registrations = ResourceInformer._registrations.Remove(this);
                    Complete();
                }
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
