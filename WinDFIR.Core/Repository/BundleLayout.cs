using System.Security.Cryptography;
using System.Text.Json;

namespace WinDFIR.Core.Repository;

/// <summary>
/// Shared, transport-agnostic helpers for locating, identifying, hashing, and path-guarding snapshot
/// bundles. Used by every <see cref="IArtifactSink"/> implementation and the intake server so the rules
/// (what counts as a bundle, how a path segment is sanitized, how containment is enforced) live in one place.
/// </summary>
internal static class BundleLayout
{
    /// <summary>
    /// Resolves a bundle directory: the path itself if it holds timeline.json, else the most recent snapshot_*
    /// child that does (names are snapshot_yyyyMMdd_HHmmss, so descending order = newest first). Picking the
    /// newest avoids silently publishing a stale earlier export when the parent accumulated several.
    /// </summary>
    public static string? ResolveBundleDirectory(string path)
    {
        var dir = Path.GetFullPath(path);
        if (!Directory.Exists(dir))
            return null;

        if (File.Exists(Path.Combine(dir, "timeline.json")))
            return dir;

        try
        {
            foreach (var sub in Directory.GetDirectories(dir, "snapshot_*").OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(sub, "timeline.json")))
                    return sub;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>Reads the collectionId and hostname from a bundle's manifest.json (either may be null if absent).</summary>
    public static async Task<(string? collectionId, string? hostname)> ReadIdentityAsync(
        string bundleDir, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(bundleDir, "manifest.json");
        if (!File.Exists(manifestPath))
            return (null, null);

        await using var stream = File.OpenRead(manifestPath);
        using var doc = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = doc.RootElement;

        var collectionId = root.TryGetProperty("collectionId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;

        string? hostname = null;
        if (root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object
            && host.TryGetProperty("hostname", out var hn) && hn.ValueKind == JsonValueKind.String)
        {
            hostname = hn.GetString();
        }

        return (collectionId, hostname);
    }

    public static async Task<string> Sha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>Bundle-relative path with forward slashes — the stable cross-transport key for a file.</summary>
    public static string RelativeKey(string bundleDir, string filePath) =>
        Path.GetRelativePath(bundleDir, filePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    /// <summary>
    /// Makes a manifest-derived value safe as a single path segment: invalid filename characters (including
    /// the path separators on Windows) become '_', and leading/trailing dots are stripped so a hostile or
    /// malformed value can never become "." / ".." or escape the repository root.
    /// </summary>
    public static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().Trim('.').Trim();
        if (string.IsNullOrEmpty(cleaned))
            return "_";

        // A Windows reserved device name (CON, NUL, COM1...) is illegal as a path segment even with an
        // extension, and would make Directory.Create/Move throw — wedging that host/collection. A hostile or
        // unlucky manifest value could land on one, so neutralize it with a prefix.
        var baseName = cleaned.Split('.', 2)[0];
        if (ReservedDeviceNames.Contains(baseName))
            return "_" + cleaned;

        return cleaned;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>True if <paramref name="path"/> resolves to a location inside <paramref name="directory"/>.</summary>
    public static bool IsWithinDirectory(string path, string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!root.EndsWith(Path.DirectorySeparatorChar) && !root.EndsWith(Path.AltDirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
