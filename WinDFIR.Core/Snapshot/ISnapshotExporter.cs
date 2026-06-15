using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Interface for exporting snapshot bundles.
/// Per specification: snapshot_bundle structure with timeline.json, entities.json, raw/, manifest.json, hashes.txt
/// </summary>
public interface ISnapshotExporter
{
    /// <summary>
    /// Exports a snapshot bundle to the specified directory.
    /// </summary>
    /// <param name="options">Optional: manifest extras (e.g. known risks, ETW drop counts).</param>
    /// <returns>The full path of the finalized snapshot bundle directory (e.g. ...\snapshot_yyyyMMdd_HHmmss).</returns>
    Task<string> ExportAsync(IActivityIndex index, string outputDirectory, SnapshotExportOptions? options = null, CancellationToken cancellationToken = default);
}
