using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Traffic-light level for a single collection-trust signal or the overall assessment.
/// Ordering matters: <see cref="Red"/> &gt; <see cref="Amber"/> &gt; <see cref="Green"/> for aggregation.
/// </summary>
public enum CollectionTrustLevel
{
    Green = 0,
    Amber = 1,
    Red = 2
}

/// <summary>
/// One assessed dimension of how much an analyst can trust a snapshot's completeness/fidelity.
/// </summary>
public sealed class CollectionTrustSignal
{
    public required string Name { get; init; }

    public required CollectionTrustLevel Level { get; init; }

    /// <summary>
    /// True when the underlying value could not be determined from the manifest (e.g. an older
    /// bundle that predates the field). Unknown signals are treated as <see cref="CollectionTrustLevel.Amber"/>
    /// for aggregation — in forensics, "cannot confirm complete" must not read as "complete".
    /// </summary>
    public bool IsUnknown { get; init; }

    /// <summary>Human-readable explanation shown in the dashboard.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// A red/amber/green view of snapshot collection trust, derived from <c>manifest.json</c>
/// (collectionSummary / preflight / completeness) plus the integrity-verification status.
/// Surfaces manifest-level caveats — capped events, dropped ETW, incomplete artifact copies,
/// limited privilege, non-forensic registry — that are otherwise buried in JSON.
/// </summary>
public sealed class CollectionTrustReport
{
    public required CollectionTrustLevel OverallLevel { get; init; }

    /// <summary>False when no readable manifest.json was found; the report then carries a single Red signal.</summary>
    public bool ManifestPresent { get; init; }

    public string? SnapshotDirectory { get; init; }

    public IReadOnlyList<CollectionTrustSignal> Signals { get; init; } = Array.Empty<CollectionTrustSignal>();

    public IReadOnlyList<string> KnownLimitations { get; init; } = Array.Empty<string>();

    // Headline values for display (best-effort; null when absent).
    public string? Hostname { get; init; }
    public string? ToolVersion { get; init; }
    public string? CollectionTimeUtc { get; init; }
    public long? SourceEventCount { get; init; }
    public long? ExportedEventCount { get; init; }

    public int RedCount => Signals.Count(s => s.Level == CollectionTrustLevel.Red);
    public int AmberCount => Signals.Count(s => s.Level == CollectionTrustLevel.Amber);
}

/// <summary>
/// Builds a <see cref="CollectionTrustReport"/> from a snapshot bundle. Pure/Core-side so the
/// rules are unit-testable independent of the UI.
/// </summary>
public static class CollectionTrustAssessor
{
    /// <summary>
    /// Assesses the snapshot bundle located at <paramref name="folderPath"/> (the folder containing
    /// manifest.json/timeline.json, or a parent holding a <c>snapshot_*</c> child).
    /// </summary>
    /// <param name="folderPath">Snapshot folder or its parent.</param>
    /// <param name="integrityStatus">Optional result of <see cref="SnapshotIntegrityVerifier"/>; folded in as a signal.</param>
    public static CollectionTrustReport AssessFolder(string folderPath, SnapshotIntegrityStatus? integrityStatus = null)
    {
        var directory = ResolveSnapshotDirectory(folderPath);
        var manifestPath = directory is null ? null : Path.Combine(directory, "manifest.json");

        if (manifestPath is null || !File.Exists(manifestPath))
            return BuildMissingManifestReport(directory, integrityStatus);

        string manifestJson;
        try
        {
            manifestJson = File.ReadAllText(manifestPath);
        }
        catch (Exception)
        {
            return BuildMissingManifestReport(directory, integrityStatus);
        }

        return AssessManifestJson(manifestJson, integrityStatus, directory);
    }

    /// <summary>Assesses a manifest.json document directly (used by tests and callers that already hold the JSON).</summary>
    public static CollectionTrustReport AssessManifestJson(
        string manifestJson,
        SnapshotIntegrityStatus? integrityStatus = null,
        string? snapshotDirectory = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(manifestJson);
        }
        catch (JsonException)
        {
            return BuildMissingManifestReport(snapshotDirectory, integrityStatus);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return BuildMissingManifestReport(snapshotDirectory, integrityStatus);

            var summary = GetObject(root, "collectionSummary");
            var preflight = GetObject(root, "preflight");

            var signals = new List<CollectionTrustSignal>();

            if (integrityStatus.HasValue)
                signals.Add(BuildIntegritySignal(integrityStatus.Value));

            signals.Add(BuildCompletenessSignal(root));
            signals.Add(BuildEventCapSignal(summary));
            signals.Add(BuildEtwDropSignal(summary));
            signals.Add(BuildUiBackpressureSignal(summary));
            signals.Add(BuildArtifactCopySignal(summary));
            signals.Add(BuildVssArtifactSignal(summary));
            signals.Add(BuildPreflightSignal(preflight));
            signals.Add(BuildPrivilegeSignal(preflight));
            signals.Add(BuildRegistryModeSignal(root, preflight));
            signals.Add(BuildModeProfileSignal(root, preflight));

            var overall = AggregateLevel(signals);

            return new CollectionTrustReport
            {
                OverallLevel = overall,
                ManifestPresent = true,
                SnapshotDirectory = snapshotDirectory,
                Signals = signals,
                KnownLimitations = ReadKnownLimitations(root),
                Hostname = GetString(GetObject(root, "host"), "hostname"),
                ToolVersion = GetString(root, "toolVersion"),
                CollectionTimeUtc = GetString(root, "collectionTime"),
                SourceEventCount = GetInt64(summary, "sourceEventCount"),
                ExportedEventCount = GetInt64(summary, "exportedEventCount")
            };
        }
    }

    // ---- Signal builders -------------------------------------------------

    private static CollectionTrustSignal BuildIntegritySignal(SnapshotIntegrityStatus status)
    {
        return status switch
        {
            SnapshotIntegrityStatus.Verified => Green("Integrity", "hashes.txt verified against bundle contents."),
            SnapshotIntegrityStatus.Unverified => Amber("Integrity", "Bundle could not be hash-verified (hashes.txt missing or unreadable)."),
            _ => Red("Integrity", "Hash verification FAILED — bundle may be tampered with or corrupted.")
        };
    }

    private static CollectionTrustSignal BuildCompletenessSignal(JsonElement root)
    {
        var complete = GetBool(root, "complete");
        if (complete == true)
            return Green("Bundle completeness", "Export finished and marked complete.");
        if (complete == false)
            return Red("Bundle completeness", "Manifest marks the bundle as NOT complete — export did not finish.");
        return Unknown("Bundle completeness", "No completeness marker; bundle may predate the 'complete' flag.");
    }

    private static CollectionTrustSignal BuildEventCapSignal(JsonElement? summary)
    {
        var capped = GetBool(summary, "wasEventCountCapped");
        var source = GetInt64(summary, "sourceEventCount");
        var exported = GetInt64(summary, "exportedEventCount");
        var cap = GetInt64(summary, "eventCap");

        if (capped == true)
        {
            var lost = source.HasValue && exported.HasValue ? source.Value - exported.Value : (long?)null;
            var detail = lost.HasValue
                ? $"Event count was capped: {lost:N0} of {source:N0} events were NOT exported (cap {cap:N0})."
                : $"Event count was capped at the export limit ({cap:N0}); some events were not exported.";
            return Red("Event truncation", detail);
        }
        if (capped == false)
            return Green("Event truncation", exported.HasValue
                ? $"All {exported:N0} source events exported; no cap hit."
                : "No event cap was hit.");
        return Unknown("Event truncation", "Cap state unknown (source event count not recorded); cannot confirm the full timeline was exported.");
    }

    private static CollectionTrustSignal BuildEtwDropSignal(JsonElement? summary)
    {
        var dropped = GetInt64(summary, "etwDroppedEventTotal");
        if (!dropped.HasValue)
            return Unknown("ETW completeness", "ETW drop total not recorded; cannot confirm no high-frequency events were lost.");
        if (dropped.Value <= 0)
            return Green("ETW completeness", "No ETW events were dropped by throttling/ingest backpressure.");
        return Amber("ETW completeness",
            $"{dropped.Value:N0} ETW event(s) dropped (throttle/BurstQueue). \"Not seen\" does not mean \"not present.\"");
    }

    private static CollectionTrustSignal BuildUiBackpressureSignal(JsonElement? summary)
    {
        var dropped = GetInt64(summary, "uiBackpressureDroppedTotal");
        if (!dropped.HasValue)
            return Green("UI render backpressure", "No UI-backpressure drops recorded (does not affect persisted timeline).");
        if (dropped.Value <= 0)
            return Green("UI render backpressure", "No UI render events were dropped.");
        // Per LIMITATIONS §10a these drops affect only live visual completeness, NOT the persisted index/snapshot.
        return Amber("UI render backpressure",
            $"{dropped.Value:N0} live render event(s) dropped for responsiveness. Persisted timeline is unaffected; live view may have looked incomplete during bursts.");
    }

    private static CollectionTrustSignal BuildArtifactCopySignal(JsonElement? summary)
    {
        var failed = GetInt64(summary, "failedEvidenceReferenceCount") ?? 0;
        var skipped = GetInt64(summary, "skippedEvidenceReferenceCount") ?? 0;
        var incomplete = GetBool(summary, "wasArtifactCopyIncomplete");
        var warning = GetString(summary, "artifactCopyWarning");

        if (failed > 0)
            return Red("Raw artifact copy",
                $"{failed:N0} evidence file(s) FAILED to copy into raw/; those raw artifacts are missing from the bundle.");
        if (skipped > 0 || incomplete == true)
        {
            var parts = new List<string>();
            if (skipped > 0) parts.Add($"{skipped:N0} skipped");
            if (!string.IsNullOrWhiteSpace(warning)) parts.Add(warning!);
            var detail = parts.Count > 0
                ? $"Artifact copy incomplete ({string.Join("; ", parts)}). Some evidence references have no bundled raw file."
                : "Artifact copy reported incomplete; some evidence references have no bundled raw file.";
            return Amber("Raw artifact copy", detail);
        }
        return Green("Raw artifact copy", "All referenced raw artifacts copied into the bundle.");
    }

    private static CollectionTrustSignal BuildVssArtifactSignal(JsonElement? summary)
    {
        var copied = GetInt64(summary, "copiedArtifactFileCount") ?? 0;
        var evidenceRefs = GetInt64(summary, "evidenceReferenceCount") ?? 0;
        if (copied <= 0 && evidenceRefs <= 0)
            return Green("VSS artifact source", "No raw artifacts required copying.");

        var usedVss = GetBool(summary, "usedVssSnapshotForArtifactCopy");
        if (usedVss == true)
            return Green("VSS artifact source", "Raw artifacts copied from a VSS shadow copy (locked-file safe).");
        if (usedVss == false)
            return Amber("VSS artifact source",
                "Raw artifacts copied from live paths (no VSS); locked or in-use files may have been skipped or changed during copy.");
        return Unknown("VSS artifact source", "VSS usage for artifact copy not recorded.");
    }

    private static CollectionTrustSignal BuildPreflightSignal(JsonElement? preflight)
    {
        if (preflight is null)
            return Unknown("Preflight", "No preflight section recorded.");

        var errors = ReadStringArray(preflight, "errors");
        var warnings = ReadStringArray(preflight, "warnings");

        if (errors.Count > 0)
            return Red("Preflight", $"Preflight reported {errors.Count} error(s): {Truncate(string.Join("; ", errors))}");
        if (warnings.Count > 0)
            return Amber("Preflight", $"Preflight reported {warnings.Count} warning(s): {Truncate(string.Join("; ", warnings))}");
        return Green("Preflight", "Preflight passed with no warnings or errors.");
    }

    private static CollectionTrustSignal BuildPrivilegeSignal(JsonElement? preflight)
    {
        var admin = GetBool(preflight, "isAdministrator");
        if (admin == true)
            return Green("Privilege", "Collected with Administrator privileges.");
        if (admin == false)
            return Amber("Privilege",
                "Not Administrator: some hives, protected event logs, handles, or drivers may have been inaccessible.");
        return Unknown("Privilege", "Privilege level not recorded.");
    }

    private static CollectionTrustSignal BuildRegistryModeSignal(JsonElement root, JsonElement? preflight)
    {
        var mode = GetString(root, "registryMode") ?? GetString(preflight, "registryMode");
        var liveEnabled = GetBool(root, "registryLiveEnabled");
        var isLive = string.Equals(mode, "live_non_forensic", StringComparison.OrdinalIgnoreCase) || liveEnabled == true;
        if (isLive)
            return Amber("Registry mode",
                "Live Registry was enabled (non-forensic). Prefer Offline Hive data as the forensic baseline.");
        if (!string.IsNullOrWhiteSpace(mode) || liveEnabled == false)
            return Green("Registry mode", "Offline-only registry (forensic baseline).");
        return Unknown("Registry mode", "Registry mode not recorded.");
    }

    private static CollectionTrustSignal BuildModeProfileSignal(JsonElement root, JsonElement? preflight)
    {
        var profile = GetString(root, "modeProfile") ?? GetString(preflight, "modeProfile");
        if (string.IsNullOrWhiteSpace(profile))
            return Unknown("Mode profile", "Mode profile not recorded.");
        if (string.Equals(profile, CollectionMetadataBuilder.CustomProfile, StringComparison.OrdinalIgnoreCase))
            return Amber("Mode profile", "Custom profile: stable-baseline assumptions may not apply.");
        return Green("Mode profile", $"Mode profile: {profile}.");
    }

    // ---- Aggregation & helpers ------------------------------------------

    private static CollectionTrustLevel AggregateLevel(IEnumerable<CollectionTrustSignal> signals)
    {
        var max = CollectionTrustLevel.Green;
        foreach (var s in signals)
        {
            // Unknown is treated as at-least-Amber caution.
            var effective = s.IsUnknown && s.Level < CollectionTrustLevel.Amber ? CollectionTrustLevel.Amber : s.Level;
            if (effective > max)
                max = effective;
        }
        return max;
    }

    private static CollectionTrustReport BuildMissingManifestReport(string? directory, SnapshotIntegrityStatus? integrityStatus)
    {
        var signals = new List<CollectionTrustSignal>();
        if (integrityStatus.HasValue)
            signals.Add(BuildIntegritySignal(integrityStatus.Value));
        signals.Add(Red("Manifest", "manifest.json is missing or unreadable; collection trust cannot be assessed."));

        return new CollectionTrustReport
        {
            OverallLevel = CollectionTrustLevel.Red,
            ManifestPresent = false,
            SnapshotDirectory = directory,
            Signals = signals
        };
    }

    private static CollectionTrustSignal Green(string name, string detail) =>
        new() { Name = name, Level = CollectionTrustLevel.Green, Detail = detail };

    private static CollectionTrustSignal Amber(string name, string detail) =>
        new() { Name = name, Level = CollectionTrustLevel.Amber, Detail = detail };

    private static CollectionTrustSignal Red(string name, string detail) =>
        new() { Name = name, Level = CollectionTrustLevel.Red, Detail = detail };

    private static CollectionTrustSignal Unknown(string name, string detail) =>
        new() { Name = name, Level = CollectionTrustLevel.Amber, IsUnknown = true, Detail = detail };

    private static string Truncate(string value, int max = 240) =>
        value.Length <= max ? value : value[..max] + "…";

    private static IReadOnlyList<string> ReadKnownLimitations(JsonElement root)
    {
        // knownLimitations may be a string[] or an object of {key: text}; accept both.
        if (!root.TryGetProperty("knownLimitations", out var el))
            return Array.Empty<string>();

        if (el.ValueKind == JsonValueKind.Array)
            return el.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray();

        if (el.ValueKind == JsonValueKind.Object)
            return el.EnumerateObject()
                .Select(p => p.Value.ValueKind == JsonValueKind.String ? $"{p.Name}: {p.Value.GetString()}" : p.Name)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            return string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : new[] { s };
        }

        return Array.Empty<string>();
    }

    private static List<string> ReadStringArray(JsonElement? parent, string name)
    {
        var result = new List<string>();
        if (parent is null || !parent.Value.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    result.Add(s);
            }
        }
        return result;
    }

    private static JsonElement? GetObject(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var el) &&
            el.ValueKind == JsonValueKind.Object)
            return el;
        return null;
    }

    private static string? GetString(JsonElement? parent, string name)
    {
        if (parent is null || !parent.Value.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static bool? GetBool(JsonElement? parent, string name)
    {
        if (parent is null || !parent.Value.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static long? GetInt64(JsonElement? parent, string name)
    {
        if (parent is null || !parent.Value.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v))
            return v;
        if (el.ValueKind == JsonValueKind.String &&
            long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
            return sv;
        return null;
    }

    private static string? ResolveSnapshotDirectory(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return null;

        var directory = Path.GetFullPath(folderPath);
        if (File.Exists(Path.Combine(directory, "manifest.json")) || File.Exists(Path.Combine(directory, "timeline.json")))
            return directory;

        try
        {
            foreach (var sub in Directory.GetDirectories(directory, "snapshot_*").OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(sub, "manifest.json")) || File.Exists(Path.Combine(sub, "timeline.json")))
                    return sub;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
