using System.Globalization;
using Registry;
using Registry.Abstractions;
using WinDFIR.Core.Entities;
using WinDFIR.Core.IO;
using WinDFIR.Core.Snapshot;
using WinDFIR.Providers.Parsers;
using System.Linq;
using RegistryKeyAbstraction = Registry.Abstractions.RegistryKey;

namespace WinDFIR.Providers;

/// <summary>
/// Offline registry hive provider scaffold (read-only).
/// Emits basic hive presence events to bootstrap offline parsing workflow.
/// Supports VSS snapshot and optional raw disk read (AddRawHive) when offset/size are known.
/// </summary>
public class OfflineHiveRegistryProvider : IProvider
{
    public string Name => "OfflineHiveRegistryProvider";
    public event EventHandler<ActivityEvent>? EventProduced;

    private readonly List<string> _hivePaths = new();
    private readonly List<string> _rawTempFiles = new();
    private static bool _encodingRegistered;
    private VssSnapshotService? _snapshotService;
    private VssSnapshotContext? _snapshotContext;

    public IReadOnlyList<string> HivePaths => _hivePaths.AsReadOnly();

    public void AddHivePath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _hivePaths.Add(path);
        }
    }

    /// <summary>
    /// Add a hive by reading a byte range from a physical drive (raw disk read).
    /// Requires Administrator. Offset/size typically from MFT or another tool.
    /// </summary>
    /// <param name="driveNumber">0-based physical drive number.</param>
    /// <param name="offsetBytes">Byte offset on the drive (sector-aligned recommended).</param>
    /// <param name="sizeBytes">Size of the hive in bytes.</param>
    /// <param name="hiveName">Name for query selection (e.g. SYSTEM, SOFTWARE).</param>
    /// <returns>True if read and temp file added; false on failure.</returns>
    public bool AddRawHive(int driveNumber, long offsetBytes, int sizeBytes, string hiveName = "SYSTEM")
    {
        var bytes = RawDiskReader.ReadBytes(driveNumber, offsetBytes, sizeBytes);
        if (bytes == null || bytes.Length == 0)
            return false;
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "HostWitness_" + hiveName + "_" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(tempPath, bytes);
            _rawTempFiles.Add(tempPath);
            _hivePaths.Add(tempPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void AddDefaultHivePaths()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemConfig = Path.Combine(systemRoot, "System32", "config");
        AddHivePath(Path.Combine(systemConfig, "SYSTEM"));
        AddHivePath(Path.Combine(systemConfig, "SOFTWARE"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddHivePath(Path.Combine(userProfile, "NTUSER.DAT"));
        AddHivePath(Path.Combine(userProfile, "AppData", "Local", "Microsoft", "Windows", "USRCLASS.DAT"));
    }

    public void SetSnapshotService(VssSnapshotService snapshotService)
    {
        _snapshotService = snapshotService;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (_snapshotContext == null && _snapshotService != null)
            {
                _snapshotContext = _snapshotService.TryCreateContextForPaths(_hivePaths, out var warning);
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    EmitSnapshotWarning(warning);
                }
                if (_snapshotContext == null)
                {
                    EmitSnapshotWarning("VSS snapshot unavailable; using live hive paths (run as Administrator and ensure Volume Shadow Copy service is running for snapshot support).");
                }
            }

            var distinctPaths = _hivePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var snapshotCount = distinctPaths.Count(p => _snapshotContext?.HasSnapshotForPath(p) == true);
            var consistencyScope = _snapshotContext == null
                ? "SingleLive"
                : (snapshotCount == 0 ? "SingleLive" : (snapshotCount == distinctPaths.Count ? "SingleSnapshot" : "Mixed"));
            if (consistencyScope == "Mixed")
                EmitSnapshotWarning("Mixed consistency: some hives from VSS snapshot, some from live paths; analysis may not be from a single point in time.");

            foreach (var hivePath in distinctPaths)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    var resolvedPath = _snapshotContext?.ResolvePath(hivePath) ?? hivePath;
                    if (!File.Exists(resolvedPath))
                        continue;

                    if (!_encodingRegistered)
                    {
                        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                        _encodingRegistered = true;
                    }

                    var hive = new RegistryHive(resolvedPath);
                    hive.ParseHive();
                    var root = hive.Root;

                    if (root == null)
                        continue;

                    var hiveName = Path.GetFileName(hivePath);
                    if (hiveName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        hiveName = Path.GetFileNameWithoutExtension(hivePath);
                    var usedSnapshot = _snapshotContext != null && _snapshotContext.HasSnapshotForPath(hivePath);
                    var queries = GetHiveQueries(hiveName);

                    foreach (var query in queries)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        foreach (var key in EnumerateKeys(root, query.Path, query.Recursive))
                        {
                            EmitKeyEvents(hivePath, resolvedPath, hiveName, key, query.Name, usedSnapshot, consistencyScope);
                        }
                    }
                }
                catch
                {
                    // Skip hives we can't read
                }
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _snapshotContext?.Dispose();
        _snapshotContext = null;
        foreach (var temp in _rawTempFiles)
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch { /* ignore */ }
        }
        _rawTempFiles.Clear();
        return Task.CompletedTask;
    }

    private record OfflineRegistryQuery(string Name, string Path, bool Recursive);

    private static List<OfflineRegistryQuery> GetHiveQueries(string hiveName)
    {
        var queries = new List<OfflineRegistryQuery>();

        if (hiveName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            queries.Add(new OfflineRegistryQuery("Services", @"ControlSet001\Services", true));
            queries.Add(new OfflineRegistryQuery("Services", @"ControlSet002\Services", true));
            queries.Add(new OfflineRegistryQuery("MountedDevices", @"MountedDevices", false));
            queries.Add(new OfflineRegistryQuery("AppCompatCache", @"ControlSet001\Control\Session Manager\AppCompatCache", false));
            queries.Add(new OfflineRegistryQuery("AppCompatCache", @"ControlSet002\Control\Session Manager\AppCompatCache", false));
            queries.Add(new OfflineRegistryQuery("TimeZoneInformation", @"ControlSet001\Control\TimeZoneInformation", false));
            queries.Add(new OfflineRegistryQuery("TimeZoneInformation", @"ControlSet002\Control\TimeZoneInformation", false));
            queries.Add(new OfflineRegistryQuery("ComputerName", @"ControlSet001\Control\ComputerName\ComputerName", false));
            queries.Add(new OfflineRegistryQuery("ComputerName", @"ControlSet002\Control\ComputerName\ComputerName", false));
            queries.Add(new OfflineRegistryQuery("Select", @"Select", false));
            queries.Add(new OfflineRegistryQuery("Windows", @"ControlSet001\Control\Windows", false));
            queries.Add(new OfflineRegistryQuery("Windows", @"ControlSet002\Control\Windows", false));
            queries.Add(new OfflineRegistryQuery("WmiNamespaceSecurity", @"ControlSet001\Control\WMI\Security", true));
            queries.Add(new OfflineRegistryQuery("WmiNamespaceSecurity", @"ControlSet002\Control\WMI\Security", true));
        }
        else if (hiveName.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
        {
            queries.Add(new OfflineRegistryQuery("Run", @"Microsoft\Windows\CurrentVersion\Run", false));
            queries.Add(new OfflineRegistryQuery("RunOnce", @"Microsoft\Windows\CurrentVersion\RunOnce", false));
            queries.Add(new OfflineRegistryQuery("RunServices", @"Microsoft\Windows\CurrentVersion\RunServices", false));
            queries.Add(new OfflineRegistryQuery("RunServicesOnce", @"Microsoft\Windows\CurrentVersion\RunServicesOnce", false));
            queries.Add(new OfflineRegistryQuery("PoliciesRun", @"Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", false));
            queries.Add(new OfflineRegistryQuery("StartupApprovedRun", @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false));
            queries.Add(new OfflineRegistryQuery("Winlogon", @"Microsoft\Windows NT\CurrentVersion\Winlogon", false));
            queries.Add(new OfflineRegistryQuery("IFEO", @"Microsoft\Windows NT\CurrentVersion\Image File Execution Options", true));
            queries.Add(new OfflineRegistryQuery("Uninstall", @"Microsoft\Windows\CurrentVersion\Uninstall", true));
            queries.Add(new OfflineRegistryQuery("AppCompatLayers", @"Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", false));
            queries.Add(new OfflineRegistryQuery("ProfileList", @"Microsoft\Windows NT\CurrentVersion\ProfileList", true));
            queries.Add(new OfflineRegistryQuery("InternetSettingsConnections", @"Microsoft\Windows\CurrentVersion\Internet Settings\Connections", false));
            queries.Add(new OfflineRegistryQuery("BitsClient", @"Microsoft\Windows\CurrentVersion\BITS", true));
            queries.Add(new OfflineRegistryQuery("WmiCimom", @"Microsoft\WBEM\CIMOM", false));
            queries.Add(new OfflineRegistryQuery("SrumRegistry", @"Microsoft\Windows NT\CurrentVersion\SRUM", true));
            queries.Add(new OfflineRegistryQuery("TaskCache", @"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks", true));
        }
        else if (hiveName.Equals("NTUSER.DAT", StringComparison.OrdinalIgnoreCase))
        {
            queries.Add(new OfflineRegistryQuery("User Run", @"Software\Microsoft\Windows\CurrentVersion\Run", false));
            queries.Add(new OfflineRegistryQuery("User RunOnce", @"Software\Microsoft\Windows\CurrentVersion\RunOnce", false));
            queries.Add(new OfflineRegistryQuery("RunMRU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", false));
            queries.Add(new OfflineRegistryQuery("TypedURLs", @"Software\Microsoft\Internet Explorer\TypedURLs", false));
            queries.Add(new OfflineRegistryQuery("TypedPaths", @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths", false));
            queries.Add(new OfflineRegistryQuery("RecentDocs", @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", true));
            queries.Add(new OfflineRegistryQuery("UserAssist", @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist", true));
            queries.Add(new OfflineRegistryQuery("MountPoints2", @"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2", true));
            queries.Add(new OfflineRegistryQuery("OpenSavePidlMRU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU", true));
            queries.Add(new OfflineRegistryQuery("OpenSaveMRU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSaveMRU", true));
            queries.Add(new OfflineRegistryQuery("LastVisitedPidlMRU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU", true));
            queries.Add(new OfflineRegistryQuery("Streams", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Streams", true));
        }
        else if (hiveName.Equals("USRCLASS.DAT", StringComparison.OrdinalIgnoreCase))
        {
            queries.Add(new OfflineRegistryQuery("MuiCache", @"Local Settings\Software\Microsoft\Windows\Shell\MuiCache", false));
            queries.Add(new OfflineRegistryQuery("BagMRU", @"Local Settings\Software\Microsoft\Windows\Shell\BagMRU", true));
            queries.Add(new OfflineRegistryQuery("Bags", @"Local Settings\Software\Microsoft\Windows\Shell\Bags", true));
        }

        return queries;
    }

    /// <summary>Enumerates keys under path without building a full list; yields one-by-one to reduce memory for large subtrees.</summary>
    private static IEnumerable<RegistryKeyAbstraction> EnumerateKeys(RegistryKeyAbstraction root, string path, bool recursive)
    {
        var startKey = FindKey(root, path);
        if (startKey == null)
            yield break;

        if (recursive)
        {
            foreach (var k in TraverseYield(startKey))
                yield return k;
        }
        else
        {
            yield return startKey;
        }
    }

    private static IEnumerable<RegistryKeyAbstraction> TraverseYield(RegistryKeyAbstraction key)
    {
        yield return key;
        foreach (var subKey in key.SubKeys)
        {
            foreach (var k in TraverseYield(subKey))
                yield return k;
        }
    }

    private static RegistryKeyAbstraction? FindKey(RegistryKeyAbstraction root, string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        foreach (var part in parts)
        {
            var next = current.SubKeys.FirstOrDefault(k =>
                k.KeyName.Equals(part, StringComparison.OrdinalIgnoreCase));

            if (next == null)
                return null;

            current = next;
        }

        return current;
    }

    private void EmitKeyEvents(string hivePath, string resolvedPath, string hiveName, RegistryKeyAbstraction key, string queryName, bool usedSnapshot, string consistencyScope)
    {
        var keyPath = $"{hiveName}\\{key.KeyPath}";
        var keyTime = key.LastWriteTime?.UtcDateTime ?? DateTime.UtcNow;

        var baseFields = new Dictionary<string, object>
        {
            ["HiveName"] = hiveName,
            ["HivePath"] = hivePath,
            ["SnapshotPath"] = resolvedPath,
            ["KeyPath"] = key.KeyPath,
            ["QueryName"] = queryName,
            ["Mode"] = "Offline",
            ["Parser"] = "Registry",
            ["OfflineHiveSource"] = usedSnapshot ? "VSS" : "Live",
            ["ConsistencyScope"] = consistencyScope
        };
        if (usedSnapshot && _snapshotContext != null)
            baseFields["SnapshotTimeUtc"] = _snapshotContext.CreationTimeUtc;

        foreach (var value in key.Values)
        {
            var valueData = value.ValueData?.ToString() ?? string.Empty;
            if (value.ValueDataRaw is byte[] bytes)
            {
                valueData = BitConverter.ToString(bytes).Replace("-", " ");
                if (valueData.Length > 200) valueData = valueData.Substring(0, 200) + "...";
            }

            foreach (var evt in BuildOfflineRegistryValueEvents(
                baseFields,
                keyPath,
                keyTime,
                queryName,
                key.KeyPath,
                value.ValueName,
                value.ValueType.ToString(),
                valueData,
                value.ValueDataRaw as byte[],
                resolvedPath,
                null))
            {
                EventProduced?.Invoke(this, evt);
            }
        }
    }

    /// <summary>
    /// Test seam: builds the same <see cref="ActivityEvent"/> sequence as live offline hive enumeration for one registry value.
    /// </summary>
    internal static List<ActivityEvent> BuildOfflineRegistryValueEventsForTest(
        Dictionary<string, object> baseFields,
        string registryObjectKeyPathFull,
        DateTime keyLastWriteUtc,
        string queryName,
        string keyPathRelative,
        string valueName,
        string valueType,
        string valueDataDisplay,
        byte[]? valueRaw,
        string resolvedPath,
        Func<byte[], IReadOnlyList<ShimCacheEntry>>? shimParserOverride = null) =>
        BuildOfflineRegistryValueEvents(
            baseFields,
            registryObjectKeyPathFull,
            keyLastWriteUtc,
            queryName,
            keyPathRelative,
            valueName,
            valueType,
            valueDataDisplay,
            valueRaw,
            resolvedPath,
            shimParserOverride);

    private static List<ActivityEvent> BuildOfflineRegistryValueEvents(
        Dictionary<string, object> baseFields,
        string registryObjectKeyPathFull,
        DateTime keyLastWriteUtc,
        string queryName,
        string keyPathRelative,
        string valueName,
        string valueType,
        string valueDataDisplay,
        byte[]? valueRaw,
        string resolvedPath,
        Func<byte[], IReadOnlyList<ShimCacheEntry>>? shimParserOverride)
    {
        var events = new List<ActivityEvent>();
        var fields = new Dictionary<string, object>(baseFields)
        {
            ["ValueName"] = valueName,
            ["ValueData"] = valueDataDisplay,
            ["ValueType"] = valueType
        };

        var summary = $"{queryName}: {keyPathRelative}\\{valueName}";
        var primaryTimestamp = keyLastWriteUtc;
        OfflineRegistryPersistenceEnrichment.Apply(queryName, keyPathRelative, valueName, valueDataDisplay, valueRaw, fields,
            ref summary);

        // BAM/DAM records per-user last-execution FILETIME under Services\bam|dam\...\UserSettings\<SID>.
        // These keys are reached by the recursive Services query; decode in place and anchor the event
        // at the actual execution time so it lands correctly on the timeline.
        if (BamDamParser.IsBamUserSettingsKey(keyPathRelative, out var bamComponent)
            && BamDamParser.TryDecodeLastExecution(valueName, valueRaw, out var bamLastExec))
        {
            var sid = BamDamParser.ExtractUserSid(keyPathRelative);
            fields["OfflineHiveDecoded"] = bamComponent;
            fields["BamComponent"] = bamComponent;
            fields["BamUserSid"] = sid;
            fields["BamExecutablePath"] = valueName;
            fields["BamLastExecutionUtc"] = bamLastExec.ToString("o", CultureInfo.InvariantCulture);
            primaryTimestamp = bamLastExec;
            summary = $"{bamComponent} last-exec: {valueName} @ {bamLastExec.ToString("u", CultureInfo.InvariantCulture)} (user {sid})";
        }

        // TaskCache records Scheduled Task registration metadata under ...\Schedule\TaskCache\Tasks\{GUID}.
        // The recursive TaskCache query reaches each per-task key; decode the safe fields in place — the Path
        // string and the FILETIMEs in DynamicInfo. Anchor the DynamicInfo event at last-run (else creation)
        // so it lands on the timeline at execution time, mirroring BAM/DAM.
        if (TaskCacheParser.IsTaskCacheTaskKey(keyPathRelative, out var taskGuid))
        {
            fields["TaskCache_Guid"] = taskGuid;
            if (valueName.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                fields["OfflineHiveDecoded"] = "TaskCache";
                fields["TaskCache_Path"] = valueDataDisplay;
                summary = $"TaskCache task: {valueDataDisplay} ({taskGuid})";
            }
            else if (valueName.Equals("DynamicInfo", StringComparison.OrdinalIgnoreCase)
                && TaskCacheParser.TryDecodeDynamicInfo(valueRaw, out var tcCreated, out var tcLastRun, out var tcLastSuccess))
            {
                fields["OfflineHiveDecoded"] = "TaskCache";
                if (tcCreated.HasValue)
                    fields["TaskCache_CreatedUtc"] = tcCreated.Value.ToString("o", CultureInfo.InvariantCulture);
                if (tcLastRun.HasValue)
                    fields["TaskCache_LastRunUtc"] = tcLastRun.Value.ToString("o", CultureInfo.InvariantCulture);
                if (tcLastSuccess.HasValue)
                    fields["TaskCache_LastSuccessUtc"] = tcLastSuccess.Value.ToString("o", CultureInfo.InvariantCulture);
                primaryTimestamp = tcLastRun ?? tcCreated ?? keyLastWriteUtc;
                var crTxt = tcCreated?.ToString("u", CultureInfo.InvariantCulture) ?? "n/a";
                var lrTxt = tcLastRun?.ToString("u", CultureInfo.InvariantCulture) ?? "n/a";
                summary = $"TaskCache DynamicInfo ({taskGuid}): created={crTxt}, lastRun={lrTxt}";
            }
        }
        if (queryName.Equals("UserAssist", StringComparison.OrdinalIgnoreCase)
            && keyPathRelative.EndsWith("\\Count", StringComparison.OrdinalIgnoreCase)
            && valueRaw is byte[] uaBytes
            && UserAssistParser.TryDecode(valueName, uaBytes, out var uaDecoded))
        {
            fields["OfflineHiveDecoded"] = "UserAssist";
            fields["UserAssist_DecodedName"] = uaDecoded.DecodedName;
            fields["UserAssist_RunCount"] = uaDecoded.RunCount;
            if (uaDecoded.FocusCount.HasValue)
                fields["UserAssist_FocusCount"] = uaDecoded.FocusCount.Value;
            if (uaDecoded.LastExecutionUtc.HasValue)
                fields["UserAssist_LastExecutionUtc"] = uaDecoded.LastExecutionUtc.Value.ToString("o", CultureInfo.InvariantCulture);
            fields["UserAssist_RawValueName"] = uaDecoded.RawValueName;
            var lastTxt = uaDecoded.LastExecutionUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "n/a";
            summary = $"UserAssist: {uaDecoded.DecodedName} (runs={uaDecoded.RunCount}, last={lastTxt})";
        }

        events.Add(new ActivityEvent
        {
            Category = "Registry",
            Action = "Query",
            Timestamp = primaryTimestamp,
            Summary = summary,
            ObjectRegistry = new WinDFIR.Core.Entities.RegistryKey(registryObjectKeyPathFull),
            Evidence = new List<EvidenceRef>
            {
                new EvidenceRef("RegistryHive", resolvedPath, collectedAt: DateTime.UtcNow)
            },
            Fields = fields,
            Confidence = "Medium"
        });

        if (queryName.Equals("AppCompatCache", StringComparison.OrdinalIgnoreCase)
            && valueName.Equals("AppCompatCache", StringComparison.OrdinalIgnoreCase)
            && valueRaw is byte[] shimBytes)
        {
            try
            {
                var parse = shimParserOverride ?? (b => AppCompatCacheParser.Parse(b));
                var controlSet = ExtractControlSetFromKeyPath(keyPathRelative);
                foreach (var row in parse(shimBytes))
                {
                    var rowTime = row.LastModifiedUtc ?? row.LastUpdateUtc ?? keyLastWriteUtc;
                    var rowFields = new Dictionary<string, object>(baseFields)
                    {
                        ["ValueName"] = "AppCompatCache",
                        ["ValueType"] = valueType,
                        ["ValueData"] = row.FilePath,
                        ["OfflineHiveDecoded"] = "ShimCache",
                        ["ShimCache_FilePath"] = row.FilePath,
                        ["ShimCache_ParseFormat"] = row.ParseFormat,
                        ["ShimCache_ControlSet"] = controlSet
                    };
                    if (row.LastModifiedUtc.HasValue)
                        rowFields["ShimCache_LastModifiedUtc"] = row.LastModifiedUtc.Value.ToString("o", CultureInfo.InvariantCulture);
                    if (row.LastUpdateUtc.HasValue)
                        rowFields["ShimCache_LastUpdateUtc"] = row.LastUpdateUtc.Value.ToString("o", CultureInfo.InvariantCulture);

                    events.Add(new ActivityEvent
                    {
                        Category = "Registry",
                        Action = "Query",
                        Timestamp = rowTime,
                        Summary = $"ShimCache ({row.ParseFormat}): {row.FilePath}",
                        ObjectRegistry = new WinDFIR.Core.Entities.RegistryKey(registryObjectKeyPathFull),
                        Evidence = new List<EvidenceRef>
                        {
                            new EvidenceRef("RegistryHive", resolvedPath, collectedAt: DateTime.UtcNow)
                        },
                        Fields = rowFields,
                        Confidence = "Medium"
                    });
                }
            }
            catch
            {
                // Raw event already emitted; decoding is best-effort only.
            }
        }

        return events;
    }

    private static string ExtractControlSetFromKeyPath(string relativeKeyPath)
    {
        if (string.IsNullOrEmpty(relativeKeyPath))
            return string.Empty;
        var slash = relativeKeyPath.IndexOf('\\');
        var head = slash > 0 ? relativeKeyPath[..slash] : relativeKeyPath;
        return head.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase) ? head : string.Empty;
    }

    private void EmitSnapshotWarning(string warning)
    {
        if (EventProduced is null)
            return;

        var timestamp = DateTime.UtcNow;
        var activityEvent = new ActivityEvent
        {
            Category = "System",
            Action = "Query",
            Timestamp = timestamp,
            Evidence = new List<EvidenceRef>
            {
                new EvidenceRef("VSS", "snapshot", null, timestamp)
            },
            Summary = warning,
            Fields = new Dictionary<string, object>
            {
                ["Warning"] = warning,
                ["Mode"] = "Offline",
                ["Component"] = "OfflineHiveRegistryProvider"
            },
            Confidence = "Low"
        };

        EventProduced?.Invoke(this, activityEvent);
    }
}
