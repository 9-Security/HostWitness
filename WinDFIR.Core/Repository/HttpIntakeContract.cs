using System.Text.Json;

namespace WinDFIR.Core.Repository;

/// <summary>
/// Wire contract shared by <see cref="HttpArtifactSink"/> (client) and the intake server. The protocol is a
/// small REST surface designed so an upload is idempotent and resumable at file granularity:
///   GET  bundles/{collectionId}/status   -> <see cref="BundleStatusResponse"/> (already complete? which files are staged?)
///   PUT  bundles/{collectionId}/files?path={relPath}   (header X-Content-Sha256, body = bytes) -> 200 / 400
///   POST bundles/{collectionId}/complete -> <see cref="BundleCompleteResponse"/> (integrity-gate + finalize)
/// </summary>
public static class HttpIntakeContract
{
    public const string Sha256Header = "X-Content-Sha256";

    public static string StatusPath(string collectionId) => $"bundles/{Uri.EscapeDataString(collectionId)}/status";

    public static string FilesPath(string collectionId, string relativePath) =>
        $"bundles/{Uri.EscapeDataString(collectionId)}/files?path={Uri.EscapeDataString(relativePath)}";

    public static string CompletePath(string collectionId) => $"bundles/{Uri.EscapeDataString(collectionId)}/complete";

    /// <summary>Single JSON convention (camelCase, case-insensitive) for both ends of the wire.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class BundleStatusResponse
{
    /// <summary>A verified bundle with this collectionId already exists in the repository.</summary>
    public bool Complete { get; set; }

    public string? DestinationPath { get; set; }

    /// <summary>Relative path -> sha256 of files already received into staging (for resume).</summary>
    public Dictionary<string, string> StagedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BundleCompleteResponse
{
    /// <summary>"Published", "AlreadyPresent", or "Failed" — mirrors <see cref="BundlePublishStatus"/>.</summary>
    public string Status { get; set; } = nameof(BundlePublishStatus.Failed);

    public string? DestinationPath { get; set; }

    public string? CollectionId { get; set; }

    public string? Hostname { get; set; }

    public List<string> Issues { get; set; } = new();
}
