using System.Management;
using WinDFIR.Core;
using WinDFIR.Core.Entities;

namespace WinDFIR.Providers;

/// <summary>
/// Live service provider: enumerates Windows services via WMI <c>Win32_Service</c> (a live API path) and emits
/// one event per service. One-shot static provider. Independently useful, and provides the <b>live</b> half for
/// the cross-source anomaly check against the raw SYSTEM-hive Services (a service present in the hive but not
/// reported here is a hiding/tampering indicator).
/// </summary>
public class LiveServiceProvider : IProvider
{
    public string Name => "LiveServiceProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => Enumerate(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
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

    private void Enumerate(CancellationToken cancellationToken)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, PathName, State, StartMode, ServiceType, ProcessId, StartName FROM Win32_Service");
            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    EmitService(obj);
                }
                catch
                {
                    // Skip services we cannot read.
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch (ManagementException ex)
        {
            CollectionWarnings.Add($"LiveService: WMI query failed — {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            CollectionWarnings.Add($"LiveService: access denied — {ex.Message}");
        }
        catch (Exception ex)
        {
            CollectionWarnings.Add($"LiveService: {ex.Message}");
        }
    }

    private void EmitService(ManagementObject obj)
    {
        var name = obj["Name"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return;

        var displayName = obj["DisplayName"]?.ToString() ?? string.Empty;
        var pathName = obj["PathName"]?.ToString() ?? string.Empty;
        var state = obj["State"]?.ToString() ?? string.Empty;
        var startMode = obj["StartMode"]?.ToString() ?? string.Empty;
        var serviceType = obj["ServiceType"]?.ToString() ?? string.Empty;
        var startName = obj["StartName"]?.ToString() ?? string.Empty;

        var fields = new Dictionary<string, object>
        {
            ["ServiceName"] = name,
            ["DisplayName"] = displayName,
            ["ImagePath"] = pathName,
            ["State"] = state,
            ["StartMode"] = startMode,
            ["ServiceType"] = serviceType,
            ["StartName"] = startName,
            ["Source"] = "WMI Win32_Service"
        };

        var activityEvent = new ActivityEvent
        {
            Category = "Service",
            Action = "Query",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("LiveService", $"Win32_Service:{name}", null, DateTime.UtcNow) },
            Summary = $"Service: {name} ({displayName}) — {state}/{startMode}",
            Fields = fields,
            Confidence = "High"
        };

        EventProduced?.Invoke(this, activityEvent);
    }
}
