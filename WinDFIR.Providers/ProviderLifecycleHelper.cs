using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WinDFIR.Providers;

/// <summary>Thrown when provider startup fails after rolling back already-started providers.</summary>
public sealed class ProviderStartException : Exception
{
    public ProviderStartException(Exception startException, IReadOnlyList<Exception> stopExceptions)
        : base(startException.Message, startException)
    {
        StartException = startException ?? throw new ArgumentNullException(nameof(startException));
        StopExceptions = stopExceptions ?? Array.Empty<Exception>();
    }

    public Exception StartException { get; }

    public IReadOnlyList<Exception> StopExceptions { get; }
}

public static class ProviderLifecycleHelper
{
    public static async Task StartProvidersAsync(IEnumerable<IProvider> providers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var startedProviders = new List<IProvider>();
        try
        {
            foreach (var provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await provider.StartAsync(cancellationToken);
                startedProviders.Add(provider);
            }
        }
        catch (Exception ex)
        {
            // Rollback must complete even if the caller's token is already canceled; otherwise
            // StopProvidersAsync can throw before already-started providers are stopped.
            var stopExceptions = await StopProvidersAsync(startedProviders, CancellationToken.None);
            throw new ProviderStartException(ex, stopExceptions);
        }
    }

    public static async Task<IReadOnlyList<Exception>> StopProvidersAsync(IEnumerable<IProvider> providers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var stopExceptions = new List<Exception>();
        foreach (var provider in providers.Reverse())
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await provider.StopAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopExceptions.Add(new InvalidOperationException($"Failed to stop provider '{provider.Name}'.", ex));
            }
        }

        return stopExceptions;
    }
}
