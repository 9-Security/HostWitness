using System.Security.Cryptography;

namespace WinDFIR.Core.Snapshot;

public enum SnapshotIntegrityStatus
{
    Verified,
    Unverified,
    Failed
}

public sealed class SnapshotIntegrityVerificationResult
{
    public SnapshotIntegrityStatus Status { get; init; }

    public string? SnapshotDirectory { get; init; }

    public int VerifiedFileCount { get; init; }

    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    public bool IsVerified => Status == SnapshotIntegrityStatus.Verified;
}

public static class SnapshotIntegrityVerifier
{
    private sealed record HashEntry(string Hash, string RelativePath);

    public static async Task<SnapshotIntegrityVerificationResult> VerifyFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var snapshotDirectory = ResolveSnapshotDirectory(folderPath);
        if (snapshotDirectory is null)
        {
            return new SnapshotIntegrityVerificationResult
            {
                Status = SnapshotIntegrityStatus.Failed,
                Issues = new[] { "timeline.json was not found in the selected folder." }
            };
        }

        var timelinePath = Path.Combine(snapshotDirectory, "timeline.json");
        var manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
        var hashesPath = Path.Combine(snapshotDirectory, "hashes.txt");

        var issues = new List<string>();
        if (!File.Exists(timelinePath))
            issues.Add("timeline.json is missing.");
        if (!File.Exists(manifestPath))
            issues.Add("manifest.json is missing.");

        if (issues.Count > 0)
        {
            return new SnapshotIntegrityVerificationResult
            {
                Status = SnapshotIntegrityStatus.Failed,
                SnapshotDirectory = snapshotDirectory,
                Issues = issues
            };
        }

        if (!File.Exists(hashesPath))
        {
            return new SnapshotIntegrityVerificationResult
            {
                Status = SnapshotIntegrityStatus.Unverified,
                SnapshotDirectory = snapshotDirectory,
                Issues = new[] { "hashes.txt is missing, so snapshot integrity cannot be verified." }
            };
        }

        var entries = await LoadHashEntriesAsync(hashesPath, cancellationToken);
        if (entries.Count == 0)
        {
            return new SnapshotIntegrityVerificationResult
            {
                Status = SnapshotIntegrityStatus.Unverified,
                SnapshotDirectory = snapshotDirectory,
                Issues = new[] { "hashes.txt does not contain any hash entries." }
            };
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidatePath = Path.GetFullPath(Path.Combine(snapshotDirectory, entry.RelativePath));
            if (!IsPathWithinDirectory(candidatePath, snapshotDirectory))
            {
                issues.Add($"Hash entry escapes snapshot directory: {entry.RelativePath}");
                continue;
            }

            if (!File.Exists(candidatePath))
            {
                issues.Add($"Hashed file is missing: {entry.RelativePath}");
                continue;
            }

            var actualHash = await ComputeSha256Async(candidatePath, cancellationToken);
            if (!string.Equals(actualHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                issues.Add($"Hash mismatch: {entry.RelativePath}");
        }

        return new SnapshotIntegrityVerificationResult
        {
            Status = issues.Count == 0 ? SnapshotIntegrityStatus.Verified : SnapshotIntegrityStatus.Failed,
            SnapshotDirectory = snapshotDirectory,
            VerifiedFileCount = entries.Count,
            Issues = issues
        };
    }

    private static async Task<List<HashEntry>> LoadHashEntriesAsync(string hashesPath, CancellationToken cancellationToken)
    {
        var entries = new List<HashEntry>();
        var lines = await File.ReadAllLinesAsync(hashesPath, cancellationToken);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
                continue;

            var parts = trimmed.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2)
                continue;

            entries.Add(new HashEntry(parts[0].Trim(), parts[1].Trim()));
        }

        return entries;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string? ResolveSnapshotDirectory(string folderPath)
    {
        var directory = Path.GetFullPath(folderPath);
        if (File.Exists(Path.Combine(directory, "timeline.json")))
            return directory;

        try
        {
            foreach (var subDirectory in Directory.GetDirectories(directory, "snapshot_*").OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(subDirectory, "timeline.json")))
                    return subDirectory;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = EnsureTrailingDirectorySeparator(Path.GetFullPath(directory));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
