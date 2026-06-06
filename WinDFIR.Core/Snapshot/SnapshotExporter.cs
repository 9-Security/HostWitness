using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Exports snapshot bundles in the format specified by the specification.
/// </summary>
public class SnapshotExporter : ISnapshotExporter
{
    private static readonly JsonSerializerOptions TimelineNestedSerializeOptions = new()
    {
        WriteIndented = false
    };

    private sealed class ArtifactExportResult
    {
        public IReadOnlyDictionary<string, string> RewrittenEvidenceReferences { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int RewrittenEvidenceReferenceCount { get; init; }

        public int CopiedArtifactFileCount { get; init; }

        public int SkippedEvidenceReferenceCount { get; init; }

        public int FailedEvidenceReferenceCount { get; init; }

        public bool UsedVssSnapshotForArtifactCopy { get; init; }

        public string? ArtifactCopyWarning { get; init; }

        public int ArtifactCopyWarningCount => string.IsNullOrWhiteSpace(ArtifactCopyWarning) ? 0 : 1;

        public bool WasArtifactCopyIncomplete =>
            SkippedEvidenceReferenceCount > 0 || FailedEvidenceReferenceCount > 0;
    }

    /// <summary>Maximum events to load in one export to avoid excessive memory use.</summary>
    public const int ExportMaxEvents = 500_000;

    public bool UseVssSnapshots { get; set; } = true;

    public async Task ExportAsync(IActivityIndex index, string outputDirectory, SnapshotExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshotDir = Path.Combine(outputDirectory, $"snapshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}");

        // Build the bundle in a sibling ".partial" directory and atomically rename to the final name
        // only after hashes.txt is written. A crash, I/O error, or cancellation mid-export therefore
        // never leaves a directory that looks like a complete, verifiable snapshot bundle.
        var workingDir = snapshotDir + ".partial";
        if (Directory.Exists(workingDir))
            Directory.Delete(workingDir, recursive: true);
        Directory.CreateDirectory(workingDir);

        try
        {
            // Create subdirectories
            var rawDir = Path.Combine(workingDir, "raw");
            Directory.CreateDirectory(rawDir);
            Directory.CreateDirectory(Path.Combine(rawDir, "evtx"));
            Directory.CreateDirectory(Path.Combine(rawDir, "prefetch"));
            Directory.CreateDirectory(Path.Combine(rawDir, "browser"));
            Directory.CreateDirectory(Path.Combine(rawDir, "lnk"));

            // Load events with a cap to avoid OOM on very large indexes (pre-size list to reduce reallocations).
            var allEvents = new List<ActivityEvent>(Math.Min(ExportMaxEvents, 8192));
            foreach (var e in index.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (allEvents.Count >= ExportMaxEvents)
                    break;
                allEvents.Add(e);
            }

            // Export raw artifacts first so timeline evidence can be rewritten to bundle-local paths.
            var artifactExport = await ExportRawArtifactsAsync(allEvents, workingDir, rawDir, cancellationToken);

            // Export timeline.json
            await ExportTimelineAsync(allEvents, workingDir, artifactExport.RewrittenEvidenceReferences, cancellationToken);

            // Export entities.json
            await ExportEntitiesAsync(allEvents, workingDir, cancellationToken);

            // Export manifest.json (with optional known-risks / ETW extras)
            var manifestExtras = CreateManifestExtras(options, allEvents, artifactExport);
            await ExportManifestAsync(workingDir, manifestExtras, cancellationToken);

            // Export hashes.txt (last — its presence plus the atomic rename marks a complete bundle)
            await ExportHashesAsync(workingDir, cancellationToken);

            if (Directory.Exists(snapshotDir))
                Directory.Delete(snapshotDir, recursive: true);
            Directory.Move(workingDir, snapshotDir);
        }
        catch
        {
            TryDeleteDirectory(workingDir);
            throw;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SnapshotExporter: failed to clean up partial bundle '{directory}': {ex.Message}");
        }
    }

    private static Dictionary<string, object?> CreateManifestExtras(
        SnapshotExportOptions? options,
        List<ActivityEvent> allEvents,
        ArtifactExportResult artifactExport)
    {
        var manifestExtras = options?.ManifestExtras != null
            ? new Dictionary<string, object?>(options.ManifestExtras)
            : new Dictionary<string, object?>();

        manifestExtras["collectionSummary"] = BuildCollectionSummary(
            options?.SourceEventCount,
            allEvents,
            artifactExport,
            options?.CollectionSummaryExtras);

        return manifestExtras;
    }

    private static Dictionary<string, object?> BuildCollectionSummary(
        long? sourceEventCount,
        List<ActivityEvent> allEvents,
        ArtifactExportResult artifactExport,
        IReadOnlyDictionary<string, object?>? extraSummaryFields)
    {
        var evidenceReferenceCount = allEvents.Sum(evt => evt.Evidence?.Count ?? 0);
        bool? wasEventCountCapped = sourceEventCount.HasValue
            ? sourceEventCount.Value > allEvents.Count
            : null;

        var collectionSummary = new Dictionary<string, object?>
        {
            ["sourceEventCount"] = sourceEventCount,
            ["exportedEventCount"] = allEvents.Count,
            ["eventCap"] = ExportMaxEvents,
            ["wasEventCountCapped"] = wasEventCountCapped,
            ["evidenceReferenceCount"] = evidenceReferenceCount,
            ["rewrittenEvidenceReferenceCount"] = artifactExport.RewrittenEvidenceReferenceCount,
            ["copiedArtifactFileCount"] = artifactExport.CopiedArtifactFileCount,
            ["skippedEvidenceReferenceCount"] = artifactExport.SkippedEvidenceReferenceCount,
            ["failedEvidenceReferenceCount"] = artifactExport.FailedEvidenceReferenceCount,
            ["wasArtifactCopyIncomplete"] = artifactExport.WasArtifactCopyIncomplete,
            ["usedVssSnapshotForArtifactCopy"] = artifactExport.UsedVssSnapshotForArtifactCopy,
            ["artifactCopyWarningCount"] = artifactExport.ArtifactCopyWarningCount
        };

        if (!string.IsNullOrWhiteSpace(artifactExport.ArtifactCopyWarning))
            collectionSummary["artifactCopyWarning"] = artifactExport.ArtifactCopyWarning;

        if (extraSummaryFields != null)
        {
            foreach (var kv in extraSummaryFields)
                collectionSummary[kv.Key] = kv.Value;
        }

        return collectionSummary;
    }

    private async Task ExportTimelineAsync(
        List<ActivityEvent> allEvents,
        string snapshotDir,
        IReadOnlyDictionary<string, string> rewrittenEvidenceReferences,
        CancellationToken cancellationToken)
    {
        var timelinePath = Path.Combine(snapshotDir, "timeline.json");
        await using var stream = new FileStream(
            timelinePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 262144,
            FileOptions.Asynchronous);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WritePropertyName("events");
        writer.WriteStartArray();
        foreach (var e in allEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteTimelineEvent(writer, e, rewrittenEvidenceReferences);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteTimelineEvent(
        Utf8JsonWriter writer,
        ActivityEvent e,
        IReadOnlyDictionary<string, string> rewrittenEvidenceReferences)
    {
        writer.WriteStartObject();
        writer.WriteString("timestamp", e.Timestamp.ToString("O"));
        writer.WriteString("category", e.Category ?? string.Empty);
        writer.WriteString("action", e.Action ?? string.Empty);
        if (e.SubjectProcess.HasValue)
            writer.WriteString("subjectProcess", e.SubjectProcess.Value.ToString());
        if (e.SubjectUser.HasValue)
            writer.WriteString("subjectUser", e.SubjectUser.Value.ToString());
        if (e.ObjectFile.HasValue)
            writer.WriteString("objectFile", e.ObjectFile.Value.ToString());
        if (e.ObjectRegistry.HasValue)
            writer.WriteString("objectRegistry", e.ObjectRegistry.Value.ToString());
        if (e.ObjectNetworkFlow.HasValue)
            writer.WriteString("objectNetworkFlow", e.ObjectNetworkFlow.Value.ToString());
        if (!string.IsNullOrEmpty(e.ObjectUrl))
            writer.WriteString("objectUrl", e.ObjectUrl);
        if (!string.IsNullOrEmpty(e.Summary))
            writer.WriteString("summary", e.Summary);

        writer.WritePropertyName("fields");
        JsonSerializer.Serialize(writer, e.Fields ?? new Dictionary<string, object>(), TimelineNestedSerializeOptions);

        writer.WritePropertyName("evidence");
        writer.WriteStartArray();
        foreach (var ev in e.Evidence)
        {
            writer.WriteStartObject();
            writer.WriteString("source", ev.Source);
            writer.WriteString("reference", RewriteEvidenceReference(ev.Reference, rewrittenEvidenceReferences));
            if (!string.IsNullOrEmpty(ev.Hash))
                writer.WriteString("hash", ev.Hash);
            if (ev.CollectedAt is { } ca)
                writer.WriteString("collectedAt", ca.ToString("O"));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("confidence", e.Confidence ?? "Medium");
        writer.WriteEndObject();
    }

    private static string RewriteEvidenceReference(string reference, IReadOnlyDictionary<string, string> rewrittenEvidenceReferences)
    {
        if (!string.IsNullOrWhiteSpace(reference) &&
            rewrittenEvidenceReferences.TryGetValue(reference, out var rewrittenReference) &&
            !string.IsNullOrWhiteSpace(rewrittenReference))
        {
            return rewrittenReference;
        }

        return reference;
    }

    private async Task ExportEntitiesAsync(List<ActivityEvent> allEvents, string snapshotDir, CancellationToken cancellationToken)
    {
        var processes = new HashSet<ProcessKey>();
        var users = new HashSet<UserKey>();
        var files = new HashSet<FileKey>();
        var networkFlows = new HashSet<NetworkFlowKey>();

        foreach (var evt in allEvents)
        {
            if (evt.SubjectProcess.HasValue)
                processes.Add(evt.SubjectProcess.Value);
            if (evt.SubjectUser.HasValue)
                users.Add(evt.SubjectUser.Value);
            if (evt.ObjectFile.HasValue)
                files.Add(evt.ObjectFile.Value);
            if (evt.ObjectNetworkFlow.HasValue)
                networkFlows.Add(evt.ObjectNetworkFlow.Value);
        }

        var entities = new
        {
            processes = processes.Select(p => new { bootId = p.BootId, processId = p.ProcessId, createTime = p.CreateTime.ToString("O") }),
            users = users.Select(u => new { sid = u.Sid }),
            files = files.Select(f => new
            {
                volumeSerial = f.VolumeSerial,
                fileId = f.FileId,
                path = f.Path,
                hash = f.Hash
            }),
            networkFlows = networkFlows.Select(n => new
            {
                protocol = n.Protocol,
                localEndpoint = n.LocalEndpoint,
                remoteEndpoint = n.RemoteEndpoint,
                processId = n.ProcessId,
                timeBucket = n.TimeBucket.ToString("O")
            })
        };

        var json = JsonSerializer.Serialize(entities, new JsonSerializerOptions { WriteIndented = true });
        var entitiesPath = Path.Combine(snapshotDir, "entities.json");
        await File.WriteAllTextAsync(entitiesPath, json, cancellationToken);
    }

    private async Task ExportManifestAsync(string snapshotDir, IReadOnlyDictionary<string, object?>? manifestExtras, CancellationToken cancellationToken)
    {
        var hostname = Environment.MachineName;
        var machineSid = GetMachineSid();
        var toolVersion = ResolveToolVersion(manifestExtras);

        var manifest = new Dictionary<string, object?>
        {
            ["toolVersion"] = toolVersion,
            ["collectionTime"] = DateTime.UtcNow.ToString("O"),
            ["host"] = new Dictionary<string, object?>
            {
                ["hostname"] = hostname,
                ["machineSid"] = machineSid,
                ["osVersion"] = Environment.OSVersion.ToString()
            },
            ["snapshotFormat"] = "1.0",
            // Written into the working ".partial" dir; the bundle only receives this manifest once the
            // export reaches the atomic rename, so a present "complete": true is a reliable completeness marker.
            ["complete"] = true
        };

        if (manifestExtras != null)
        {
            foreach (var kv in manifestExtras)
                manifest[kv.Key] = kv.Value;
        }

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var manifestPath = Path.Combine(snapshotDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
    }

    private static string ResolveToolVersion(IReadOnlyDictionary<string, object?>? manifestExtras)
    {
        if (manifestExtras != null
            && manifestExtras.TryGetValue("toolVersion", out var toolVersionValue)
            && toolVersionValue is string toolVersion
            && !string.IsNullOrWhiteSpace(toolVersion))
        {
            return toolVersion;
        }

        return ToolVersionProvider.GetCurrentVersion(typeof(SnapshotExporter));
    }

    private Task<ArtifactExportResult> ExportRawArtifactsAsync(
        List<ActivityEvent> allEvents,
        string snapshotDir,
        string rawDir,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var copiedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rewrittenEvidenceReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        VssSnapshotContext? snapshotContext = null;
        string? artifactCopyWarning = null;

        if (UseVssSnapshots)
        {
            var pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var evt in allEvents)
            {
                foreach (var ev in evt.Evidence)
                {
                    if (!string.IsNullOrWhiteSpace(ev.Reference))
                        pathSet.Add(ev.Reference);
                }
            }

            if (pathSet.Count > 0)
                snapshotContext = new VssSnapshotService().TryCreateContextForPaths(pathSet, out artifactCopyWarning);
        }

        var usedVssSnapshotForArtifactCopy = snapshotContext != null;
        var rewrittenEvidenceReferenceCount = 0;
        var copiedArtifactFileCount = 0;
        var skippedEvidenceReferenceCount = 0;
        var failedEvidenceReferenceCount = 0;

        using (snapshotContext)
        {
            foreach (var evt in allEvents)
            {
                foreach (var evidence in evt.Evidence)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var reference = evidence.Reference;
                    try
                    {
                        if (!TryResolveArtifactSourcePath(evidence, snapshotContext, out var resolvedReference, out var sourceIdentity))
                        {
                            skippedEvidenceReferenceCount++;
                            continue;
                        }

                        if (!copiedFiles.TryGetValue(sourceIdentity, out var relativePath))
                        {
                            var fileName = Path.GetFileName(resolvedReference);
                            var destSubDir = GetArtifactDestinationSubDirectory(evidence.Source);

                            var destDir = Path.Combine(rawDir, destSubDir);
                            Directory.CreateDirectory(destDir);

                            var destPath = Path.Combine(destDir, $"{Guid.NewGuid()}_{fileName}");
                            File.Copy(resolvedReference, destPath, true);

                            relativePath = Path.GetRelativePath(snapshotDir, destPath)
                                .Replace(Path.DirectorySeparatorChar, '/')
                                .Replace(Path.AltDirectorySeparatorChar, '/');
                            copiedFiles[sourceIdentity] = relativePath;
                            copiedArtifactFileCount++;
                        }

                        rewrittenEvidenceReferences[reference] = relativePath;
                        rewrittenEvidenceReferenceCount++;
                    }
                    catch (Exception ex)
                    {
                        failedEvidenceReferenceCount++;
                        System.Diagnostics.Debug.WriteLine($"SnapshotExporter: failed to copy '{reference}': {ex.Message}");
                        continue;
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(CreateArtifactExportResult(
            rewrittenEvidenceReferences,
            rewrittenEvidenceReferenceCount,
            copiedArtifactFileCount,
            skippedEvidenceReferenceCount,
            failedEvidenceReferenceCount,
            usedVssSnapshotForArtifactCopy,
            artifactCopyWarning));
    }

    private static ArtifactExportResult CreateArtifactExportResult(
        IReadOnlyDictionary<string, string> rewrittenEvidenceReferences,
        int rewrittenEvidenceReferenceCount,
        int copiedArtifactFileCount,
        int skippedEvidenceReferenceCount,
        int failedEvidenceReferenceCount,
        bool usedVssSnapshotForArtifactCopy,
        string? artifactCopyWarning)
    {
        return new ArtifactExportResult
        {
            RewrittenEvidenceReferences = rewrittenEvidenceReferences,
            RewrittenEvidenceReferenceCount = rewrittenEvidenceReferenceCount,
            CopiedArtifactFileCount = copiedArtifactFileCount,
            SkippedEvidenceReferenceCount = skippedEvidenceReferenceCount,
            FailedEvidenceReferenceCount = failedEvidenceReferenceCount,
            UsedVssSnapshotForArtifactCopy = usedVssSnapshotForArtifactCopy,
            ArtifactCopyWarning = artifactCopyWarning
        };
    }

    private static bool TryResolveArtifactSourcePath(
        EvidenceRef evidence,
        VssSnapshotContext? snapshotContext,
        out string resolvedReference,
        out string sourceIdentity)
    {
        resolvedReference = string.Empty;
        sourceIdentity = string.Empty;

        if (!TryGetArtifactSourcePath(evidence, out var artifactSourcePath))
            return false;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddArtifactCandidate(candidates, snapshotContext?.ResolvePath(artifactSourcePath) ?? artifactSourcePath);

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate) || !IsAllowedArtifactFile(evidence.Source, candidate))
                continue;

            resolvedReference = candidate;
            sourceIdentity = Path.GetFullPath(candidate);
            return true;
        }

        return false;
    }

    private static bool TryGetArtifactSourcePath(EvidenceRef evidence, out string artifactSourcePath)
    {
        artifactSourcePath = string.Empty;
        if (string.IsNullOrWhiteSpace(evidence.Reference))
            return false;

        switch (evidence.Source)
        {
            case "Prefetch":
            case "RecentLnk":
            case "JumpList":
            case "RegistryHive":
            case "ScheduledTask":
            case "PowerShellHistory":
                artifactSourcePath = evidence.Reference;
                return Path.IsPathRooted(artifactSourcePath);
            case "BrowserHistory":
                artifactSourcePath = TryExtractRootedArtifactPath(evidence.Reference);
                return Path.IsPathRooted(artifactSourcePath);
            default:
                return false;
        }
    }

    private static string GetArtifactDestinationSubDirectory(string source)
    {
        return source switch
        {
            "Prefetch" => "prefetch",
            "BrowserHistory" => "browser",
            "RecentLnk" or "JumpList" => "lnk",
            "RegistryHive" => "registry",
            "ScheduledTask" => "tasks",
            "PowerShellHistory" => "powershell",
            _ => "other"
        };
    }

    private static bool IsAllowedArtifactFile(string source, string path)
    {
        var fileName = Path.GetFileName(path);
        return source switch
        {
            "Prefetch" => fileName.EndsWith(".pf", StringComparison.OrdinalIgnoreCase),
            "RecentLnk" => fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase),
            "JumpList" => fileName.EndsWith(".automaticDestinations-ms", StringComparison.OrdinalIgnoreCase) ||
                          fileName.EndsWith(".customDestinations-ms", StringComparison.OrdinalIgnoreCase),
            "BrowserHistory" => IsKnownBrowserHistoryFile(fileName),
            "RegistryHive" => IsKnownRegistryHiveFile(fileName),
            // Task Scheduler definitions are extensionless under System32\Tasks (some exports use .xml).
            "ScheduledTask" => IsAllowedScheduledTaskFile(path),
            "PowerShellHistory" => fileName.Equals("ConsoleHost_history.txt", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsAllowedScheduledTaskFile(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) || ext.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownBrowserHistoryFile(string fileName)
    {
        return string.Equals(fileName, "History", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "Archived History", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "places.sqlite", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "WebCacheV01.dat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownRegistryHiveFile(string fileName)
    {
        return fileName.StartsWith("HostWitness_", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "SOFTWARE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "SAM", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "SECURITY", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "DEFAULT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "NTUSER.DAT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "USRCLASS.DAT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "AMCACHE.HVE", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddArtifactCandidate(ISet<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
            candidates.Add(candidate);
    }

    private static string TryExtractRootedArtifactPath(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || !Path.IsPathRooted(reference))
            return reference;

        var lastColon = reference.LastIndexOf(':');
        if (lastColon <= 2 || lastColon >= reference.Length - 1)
            return reference;

        var suffix = reference[(lastColon + 1)..];
        return suffix.All(char.IsDigit)
            ? reference[..lastColon]
            : reference;
    }

    private async Task ExportHashesAsync(string snapshotDir, CancellationToken cancellationToken)
    {
        var hashes = new StringBuilder();
        hashes.AppendLine("# File Hashes (SHA256)");
        hashes.AppendLine($"# Generated: {DateTime.UtcNow:O}");
        hashes.AppendLine();

        // Hash all JSON files
        var jsonFiles = new[] { "timeline.json", "entities.json", "manifest.json" };
        foreach (var file in jsonFiles)
        {
            var filePath = Path.Combine(snapshotDir, file);
            if (File.Exists(filePath))
            {
                var hash = await ComputeFileHashAsync(filePath);
                hashes.AppendLine($"{hash}  {file}");
            }
        }

        // Hash all files in raw/ directory
        var rawDir = Path.Combine(snapshotDir, "raw");
        if (Directory.Exists(rawDir))
        {
            var rawFiles = Directory.GetFiles(rawDir, "*", SearchOption.AllDirectories);
            foreach (var filePath in rawFiles)
            {
                var hash = await ComputeFileHashAsync(filePath);
                var relativePath = Path.GetRelativePath(snapshotDir, filePath);
                hashes.AppendLine($"{hash}  {relativePath}");
            }
        }

        var hashesPath = Path.Combine(snapshotDir, "hashes.txt");
        await File.WriteAllTextAsync(hashesPath, hashes.ToString(), cancellationToken);
    }

    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private string GetMachineSid()
    {
        try
        {
            var adminSid = new SecurityIdentifier(WellKnownSidType.AccountAdministratorSid, null);
            var adminSidValue = adminSid.Value;
            var lastDash = adminSidValue.LastIndexOf('-');
            if (lastDash > 0)
                return adminSidValue[..lastDash];
            return adminSidValue;
        }
        catch
        {
            // Fallback to MachineGuid if SID cannot be resolved
            try
            {
                var guid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null)
                    ?.ToString();
                if (!string.IsNullOrWhiteSpace(guid))
                    return $"MachineGuid:{guid}";
            }
            catch
            {
                // Ignore registry access failures
            }

            return "Unknown";
        }
    }
}

