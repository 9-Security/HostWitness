using WinDFIR.Core.Entities;

namespace WinDFIR.Providers;

/// <summary>
/// Base interface for all data providers.
/// Providers collect raw data and normalize it into ActivityEvents.
/// </summary>
public interface IProvider
{
    /// <summary>
    /// Provider name for identification.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Starts data collection (for live providers) or begins processing (for static providers).
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops data collection.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when a new ActivityEvent is produced.
    /// </summary>
    event EventHandler<ActivityEvent>? EventProduced;
}
