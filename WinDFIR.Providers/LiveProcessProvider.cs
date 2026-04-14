using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using ActivityEvent = WinDFIR.Core.Entities.ActivityEvent;

namespace WinDFIR.Providers;

/// <summary>
/// Live process provider: enumerates running processes and monitors process lifecycle.
/// Per specification: Process list, tree, command line, user, integrity.
/// </summary>
public class LiveProcessProvider : IProvider
{
    public string Name => "LiveProcessProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;
    private readonly Dictionary<uint, (ProcessKey Key, DateTime CreateTime, bool IsUnknown)> _processCache = new();
    private readonly object _cacheLock = new();
    private readonly ulong _bootId = BootIdProvider.GetBootId();
    private static readonly ConcurrentDictionary<string, string> HashCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> CompanyCache = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitoringTask = Task.Run(() => MonitorProcesses(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource?.Cancel();

        if (_monitoringTask != null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
        }
    }

    private async Task MonitorProcesses(CancellationToken cancellationToken)
    {
        // Initial enumeration
        await EnumerateExistingProcesses(cancellationToken);

        // Periodic refresh
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            await EnumerateExistingProcesses(cancellationToken);
        }
    }

    private Task EnumerateExistingProcesses(CancellationToken cancellationToken)
    {
        try
        {
            var processes = Process.GetProcesses();
            var processDetails = GetProcessDetailsMap();

            var currentPids = new HashSet<uint>(processes.Select(p => (uint)p.Id));

            // Check for new processes
            foreach (var process in processes)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Task.CompletedTask;

                try
                {
                    var pid = (uint)process.Id;
                    processDetails.TryGetValue(pid, out var details);
                    lock (_cacheLock)
                    {
                        if (_processCache.TryGetValue(pid, out var cached))
                        {
                            var hasCreateTime = TryGetProcessCreateTime(process, details, out var createTime);
                            if (!hasCreateTime)
                                createTime = cached.CreateTime;

                            var processKey = KeyGenerator.GenerateProcessKey(_bootId, pid, createTime);

                            if (cached.IsUnknown && hasCreateTime)
                            {
                                var knownKey = KeyGenerator.GenerateProcessKey(_bootId, pid, createTime);
                                _processCache[pid] = (knownKey, createTime, false);
                                                               if (knownKey != cached.Key)
                                {
                                    ProduceProcessStopEvent(cached.Key);
                                    ProduceProcessStartEvent(process, knownKey, details, processDetails);
                                }
                            }
                            else if (cached.CreateTime != createTime)
                            {
                                ProduceProcessStopEvent(cached.Key);
                                _processCache[pid] = (processKey, createTime, !hasCreateTime);
                                ProduceProcessStartEvent(process, processKey, details, processDetails);
                            }
                        }
                        else
                        {
                            var hasCreateTime = TryGetProcessCreateTime(process, details, out var createTime);
                            if (!hasCreateTime)
                                createTime = DateTime.UtcNow;

                            var processKey = KeyGenerator.GenerateProcessKey(_bootId, pid, createTime);
                            _processCache[pid] = (processKey, createTime, !hasCreateTime);
                            ProduceProcessStartEvent(process, processKey, details, processDetails);
                        }
                    }
                }
                catch (Exception)
                {
                    // Skip processes we can't access
                    continue;
                }
            }

            // Check for terminated processes
            lock (_cacheLock)
            {
                var terminatedPids = _processCache.Keys.Except(currentPids).ToList();
                foreach (var pid in terminatedPids)
                {
                    var processKey = _processCache[pid].Key;
                    ProduceProcessStopEvent(processKey);
                    _processCache.Remove(pid);
                }
            }
        }
        catch (Exception)
        {
            // Continue monitoring despite errors
        }

        return Task.CompletedTask;
    }

    private void ProduceProcessStartEvent(
        Process process,
        ProcessKey processKey,
        ProcessDetails? details,
        IReadOnlyDictionary<uint, ProcessDetails>? detailsMap)
    {
        try
        {
            // Prefer single WMI query when details missing (e.g. process started between GetProcesses and GetProcessDetailsMap)
            if (details == null)
                details = GetProcessDetailsSingle(process.Id);

            var commandLine = details?.CommandLine ?? GetCommandLine(process.Id);
            var imagePath = details?.ExecutablePath ?? GetExecutablePath(process.Id);
            var company = GetCompanyName(imagePath);
            var hash = ComputeFileHash(imagePath);
            var parentPid = details?.ParentProcessId ?? GetParentProcessId(process.Id) ?? 0;
            var parentImagePath = string.Empty;
            if (parentPid > 0 && detailsMap != null &&
                detailsMap.TryGetValue((uint)parentPid, out var parentRow) &&
                !string.IsNullOrWhiteSpace(parentRow.ExecutablePath))
            {
                parentImagePath = parentRow.ExecutablePath!;
            }

            var evidence = new List<EvidenceRef>
            {
                new EvidenceRef("LiveProcessProvider", $"PID:{process.Id}", null, DateTime.UtcNow)
            };

            var fields = new Dictionary<string, object>
            {
                ["ProcessName"] = process.ProcessName,
                ["ProcessId"] = process.Id,
                ["CommandLine"] = commandLine ?? string.Empty,
                ["UserName"] = "Unknown",
                ["Integrity"] = "Unknown",
                ["ImagePath"] = imagePath ?? string.Empty,
                ["Company"] = company ?? string.Empty,
                ["Hash"] = hash ?? string.Empty,
                ["WorkingSet"] = process.WorkingSet64,
                ["ParentProcessId"] = parentPid,
                ["ParentImagePath"] = parentImagePath
            };

            LiveProcessTokenHelper.TryAddTokenFields(process.Id, fields);
            LiveProcessAuthenticodeHelper.TryAddAuthenticodeFields(imagePath, fields);

            var activityEvent = new ActivityEvent
            {
                Category = "Process",
                Action = "Start",
                Timestamp = processKey.CreateTime,
                Evidence = evidence,
                SubjectProcess = processKey,
                Summary = $"Process started: {process.ProcessName} (PID: {process.Id})",
                Fields = fields,
                Confidence = "High"
            };

            EventProduced?.Invoke(this, activityEvent);
        }
        catch
        {
            // Skip if we can't get process details
        }
    }

    private void ProduceProcessStopEvent(ProcessKey processKey)
    {
        var evidence = new List<EvidenceRef>
        {
            new EvidenceRef("LiveProcessProvider", $"PID:{processKey.ProcessId}", null, DateTime.UtcNow)
        };

        var activityEvent = new ActivityEvent
        {
            Category = "Process",
            Action = "Stop",
            Timestamp = DateTime.UtcNow,
            Evidence = evidence,
            SubjectProcess = processKey,
            Summary = $"Process stopped: PID {processKey.ProcessId}",
            Fields = new Dictionary<string, object>
            {
                ["ProcessId"] = processKey.ProcessId
            },
            Confidence = "High"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    private static Dictionary<uint, ProcessDetails> GetProcessDetailsMap()
    {
        var map = new Dictionary<uint, ProcessDetails>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine, ExecutablePath, CreationDate FROM Win32_Process");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["ProcessId"] == null)
                    continue;

                var pid = Convert.ToUInt32(obj["ProcessId"]);
                int? parentPid = null;
                try
                {
                    if (obj["ParentProcessId"] != null)
                        parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                }
                catch
                {
                    // Ignore parse failures
                }

                var commandLine = obj["CommandLine"]?.ToString();
                var executablePath = obj["ExecutablePath"]?.ToString();
                DateTime? creationTime = null;
                var raw = obj["CreationDate"]?.ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        creationTime = ManagementDateTimeConverter.ToDateTime(raw).ToUniversalTime();
                    }
                    catch
                    {
                        // Ignore parse failures
                    }
                }

                map[pid] = new ProcessDetails(commandLine, executablePath, parentPid, creationTime);
            }
        }
        catch
        {
            // Ignore WMI failures
        }

        return map;
    }

    private bool TryGetProcessCreateTime(Process process, ProcessDetails? details, out DateTime createTime)
    {
        try
        {
            createTime = process.StartTime.ToUniversalTime();
            return true;
        }
        catch
        {
            // Ignore and try WMI
        }

        if (details?.CreationTimeUtc != null)
        {
            createTime = details.CreationTimeUtc.Value;
            return true;
        }

        createTime = default;
        return false;
    }

    private sealed record ProcessDetails(
        string? CommandLine,
        string? ExecutablePath,
        int? ParentProcessId,
        DateTime? CreationTimeUtc);

    /// <summary>Single WMI query for one PID to avoid 3 separate calls when process is missing from the full-table map.</summary>
    private static ProcessDetails? GetProcessDetailsSingle(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId, CommandLine, ExecutablePath, CreationDate FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (ManagementObject obj in searcher.Get())
            {
                int? parentPid = null;
                if (obj["ParentProcessId"] != null)
                    parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                DateTime? creationUtc = null;
                if (obj["CreationDate"] != null && obj["CreationDate"] is string creationStr)
                {
                    if (DateTime.TryParse(creationStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                        creationUtc = dt.ToUniversalTime();
                }
                return new ProcessDetails(
                    obj["CommandLine"]?.ToString(),
                    obj["ExecutablePath"]?.ToString(),
                    parentPid,
                    creationUtc);
            }
        }
        catch
        {
            // WMI may not be available
        }
        return null;
    }

    private string? GetCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (ManagementObject obj in searcher.Get())
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch
        {
            // WMI may not be available or process may have exited
        }
        return null;
    }

    private string? GetExecutablePath(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (ManagementObject obj in searcher.Get())
            {
                return obj["ExecutablePath"]?.ToString();
            }
        }
        catch
        {
            // WMI may not be available or access denied
        }
        return null;
    }

    private int? GetParentProcessId(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToInt32(obj["ParentProcessId"]);
            }
        }
        catch
        {
            // WMI may not be available
        }
        return null;
    }

    private static string? GetCompanyName(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        if (CompanyCache.TryGetValue(imagePath, out var cached))
            return cached;

        try
        {
            if (!File.Exists(imagePath))
                return null;

            var info = FileVersionInfo.GetVersionInfo(imagePath);
            var company = info.CompanyName ?? string.Empty;
            CompanyCache[imagePath] = company;
            return company;
        }
        catch
        {
            return null;
        }
    }

    private static string? ComputeFileHash(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        if (HashCache.TryGetValue(imagePath, out var cached))
            return cached;

        try
        {
            if (!File.Exists(imagePath))
                return null;

            using var stream = File.Open(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(stream);
            var hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            HashCache[imagePath] = hash;
            return hash;
        }
        catch (IOException ex)
        {
            WinDFIR.Core.CollectionWarnings.Add($"LiveProcess (hash): {Path.GetFileName(imagePath)} â€” {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            WinDFIR.Core.CollectionWarnings.Add($"LiveProcess (hash): {Path.GetFileName(imagePath)} â€” {ex.Message}");
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Test seam: same <see cref="ActivityEvent.Fields"/> construction as <see cref="ProduceProcessStartEvent"/>.</summary>
    internal static Dictionary<string, object> BuildLiveProcessStartFieldsForTest(
        int processId,
        string processName,
        string? commandLine,
        string? imagePath,
        int parentPid,
        string? parentImagePath,
        long workingSet,
        string? company,
        string? hash,
        Action<int, IDictionary<string, object>>? tokenEnrich = null,
        Action<string?, IDictionary<string, object>>? authenticodeEnrich = null)
    {
        var fields = new Dictionary<string, object>
        {
            ["ProcessName"] = processName,
            ["ProcessId"] = processId,
            ["CommandLine"] = commandLine ?? string.Empty,
            ["UserName"] = "Unknown",
            ["Integrity"] = "Unknown",
            ["ImagePath"] = imagePath ?? string.Empty,
            ["Company"] = company ?? string.Empty,
            ["Hash"] = hash ?? string.Empty,
            ["WorkingSet"] = workingSet,
            ["ParentProcessId"] = parentPid,
            ["ParentImagePath"] = parentImagePath ?? string.Empty
        };

        (tokenEnrich ?? LiveProcessTokenHelper.TryAddTokenFields)(processId, fields);
        (authenticodeEnrich ?? LiveProcessAuthenticodeHelper.TryAddAuthenticodeFields)(imagePath, fields);
        return fields;
    }

    /// <summary>Test seam: full Process Start <see cref="ActivityEvent"/> shape from the live provider.</summary>
    internal static ActivityEvent BuildLiveProcessStartActivityEventForTest(
        ulong bootId,
        uint processId,
        string processName,
        DateTime createTimeUtc,
        string? commandLine,
        string? imagePath,
        int parentPid,
        string? parentImagePath,
        long workingSet,
        string? company,
        string? hash,
        Action<int, IDictionary<string, object>>? tokenEnrich = null,
        Action<string?, IDictionary<string, object>>? authenticodeEnrich = null)
    {
        var key = KeyGenerator.GenerateProcessKey(bootId, processId, createTimeUtc);
        var fields = BuildLiveProcessStartFieldsForTest(
            (int)processId,
            processName,
            commandLine,
            imagePath,
            parentPid,
            parentImagePath,
            workingSet,
            company,
            hash,
            tokenEnrich,
            authenticodeEnrich);

        return new ActivityEvent
        {
            Timestamp = createTimeUtc,
            Category = "Process",
            Action = "Start",
            Evidence = new List<EvidenceRef>
            {
                new EvidenceRef("LiveProcessProvider", $"PID:{processId}", null, DateTime.UtcNow)
            },
            SubjectProcess = key,
            Summary = $"Process started: {processName} (PID: {processId})",
            Fields = fields,
            Confidence = "High"
        };
    }
}

