using System.Diagnostics;
using System.Management;
using WinDFIR.Core;
using WinDFIR.Core.Analysis;
using WinDFIR.Core.Entities;
using ActivityEvent = WinDFIR.Core.Entities.ActivityEvent;

namespace WinDFIR.Providers;

/// <summary>
/// Process cross-check provider (P6 tripwire): enumerates running processes via two independent live APIs —
/// the native process list (<see cref="Process.GetProcesses"/>, an NtQuerySystemInformation path) and WMI
/// <c>Win32_Process</c> — and flags PIDs visible to one but not the other. A process hidden from one API but
/// not the other is a classic user-mode hiding indicator. One-shot; emits <c>Category=Anomaly</c> events.
/// </summary>
/// <remarks>
/// Both snapshots are point-in-time, so a process that starts or exits in the millisecond between the two
/// enumerations would be a false positive — every discrepancy is therefore <b>re-confirmed</b> by directly
/// re-querying the API it appeared to be missing from before it is reported. Honest bound: both APIs are
/// user-mode, so a sufficiently privileged implant can hook both; this is a tripwire, not a guarantee.
/// </remarks>
public class ProcessApiCrossCheckProvider : IProvider
{
    public string Name => "ProcessApiCrossCheckProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => Run(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource?.Cancel();
        if (_processingTask != null)
        {
            try { await _processingTask; }
            catch (OperationCanceledException) { }
        }
    }

    private void Run(CancellationToken cancellationToken)
    {
        Dictionary<int, string> native;
        Dictionary<int, string> wmi;
        try
        {
            native = EnumerateNative();
            wmi = EnumerateWmi();
        }
        catch (Exception ex)
        {
            CollectionWarnings.Add($"ProcessCrossCheck: enumeration failed — {ex.Message}");
            return;
        }

        if (native.Count == 0 || wmi.Count == 0)
            return; // cannot compare

        var nativeItems = native.Select(kv => new CrossSourceItem { Key = kv.Key.ToString(), Display = $"{kv.Value} (PID {kv.Key})", Value = kv.Value });
        var wmiItems = wmi.Select(kv => new CrossSourceItem { Key = kv.Key.ToString(), Display = $"{kv.Value} (PID {kv.Key})", Value = kv.Value });

        var anomalies = CrossSourceAnomalyDetector.Compare(
            "Process", nativeItems, wmiItems, compareValues: false,
            liveSourceName: "native process list (NtQuerySystemInformation)",
            offlineSourceName: "WMI Win32_Process");

        foreach (var anomaly in anomalies)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (!int.TryParse(anomaly.Key, out var pid))
                continue;

            // Re-confirm to filter out processes that started/exited between the two snapshots.
            var missingFromNative = anomaly.Kind == CrossSourceAnomalyDetector.MissingFromLive; // in WMI, not native
            if (!ConfirmStillDiscrepant(pid, missingFromNative))
                continue;

            EmitAnomaly(anomaly, pid, missingFromNative);
        }
    }

    private static bool ConfirmStillDiscrepant(int pid, bool missingFromNative)
    {
        try
        {
            if (missingFromNative)
            {
                // Reported missing from the native list -> confirm it is genuinely not in the native list now,
                // while it is still present in WMI.
                var stillInNative = NativeHasPid(pid);
                var stillInWmi = WmiHasPid(pid);
                return !stillInNative && stillInWmi;
            }
            else
            {
                // Reported missing from WMI -> confirm still absent from WMI while present in the native list.
                var stillInWmi = WmiHasPid(pid);
                var stillInNative = NativeHasPid(pid);
                return !stillInWmi && stillInNative;
            }
        }
        catch
        {
            return false; // if we cannot confirm, do not report (avoid false positives)
        }
    }

    private static bool NativeHasPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // not running
        }
        catch
        {
            return false;
        }
    }

    private static bool WmiHasPid(int pid)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {pid}");
        using var results = searcher.Get();
        foreach (ManagementObject o in results)
        {
            o.Dispose();
            return true;
        }
        return false;
    }

    private static Dictionary<int, string> EnumerateNative()
    {
        var map = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                map[p.Id] = SafeName(p);
            }
            catch { /* skip */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    private static string SafeName(Process p)
    {
        try { return (p.ProcessName ?? string.Empty).ToLowerInvariant(); }
        catch { return string.Empty; }
    }

    private static Dictionary<int, string> EnumerateWmi()
    {
        var map = new Dictionary<int, string>();
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name FROM Win32_Process");
        using var results = searcher.Get();
        foreach (ManagementObject o in results)
        {
            try
            {
                if (o["ProcessId"] == null)
                    continue;
                var pid = Convert.ToInt32(o["ProcessId"]);
                var name = (o["Name"]?.ToString() ?? string.Empty);
                // Normalize to match Process.ProcessName (no .exe, lowercase).
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                map[pid] = name.ToLowerInvariant();
            }
            catch { /* skip */ }
            finally { o.Dispose(); }
        }
        return map;
    }

    private void EmitAnomaly(CrossSourceAnomaly anomaly, int pid, bool missingFromNative)
    {
        var fields = new Dictionary<string, object>
        {
            ["AnomalyKind"] = anomaly.Kind,
            ["AnomalyCategory"] = "Process",
            ["Key"] = anomaly.Key,
            ["ProcessId"] = pid,
            ["MissingFrom"] = missingFromNative ? "native process list" : "WMI Win32_Process",
            ["Analyzer"] = "CrossSource",
            ["Detail"] = anomaly.Detail
        };
        if (anomaly.Display != null) fields["Display"] = anomaly.Display;

        var activityEvent = new ActivityEvent
        {
            Category = "Anomaly",
            Action = "CrossSource",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("CrossSourceAnalysis", $"Process:{pid}", null, DateTime.UtcNow) },
            Summary = $"[{anomaly.Kind}] {anomaly.Display} hidden from {(missingFromNative ? "native process list" : "WMI")} — possible process hiding. Investigate.",
            Fields = fields,
            Confidence = "Low"
        };

        EventProduced?.Invoke(this, activityEvent);
    }
}
