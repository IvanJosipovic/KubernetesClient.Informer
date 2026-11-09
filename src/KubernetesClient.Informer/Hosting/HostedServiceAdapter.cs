// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;
using KubernetesClient.Informer.Client;

namespace KubernetesClient.Informer.Hosting;

/// <summary>
/// Delegates start and stop calls to service-specific interface.
/// </summary>
/// <typeparam name="TService">The service interface to delegate onto.</typeparam>
public class HostedServiceAdapter<TService> : IHostedService
    where TService : IHostedService
{
    private readonly TService _service;
    private readonly bool _startWatchingOnHostStart;

    /// <summary>
    /// Initializes a new instance of the <see cref="HostedServiceAdapter{TInterface}" /> class.
    /// </summary>
    /// <param name="service">The service interface to delegate onto.</param>
    public HostedServiceAdapter(TService service) : this(service, startWatchingOnHostStart: true)
    {
    }

    internal HostedServiceAdapter(TService service, bool startWatchingOnHostStart)
    {
        _service = service;
        _startWatchingOnHostStart = startWatchingOnHostStart;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_startWatchingOnHostStart && _service is IResourceInformer informer)
        {
            informer.StartWatching();
        }

        return _service.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _service.StopAsync(cancellationToken);
}
