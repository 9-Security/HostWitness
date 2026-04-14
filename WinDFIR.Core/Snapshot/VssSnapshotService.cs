using System.Management;
using System.Security.Principal;

namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Volume Shadow Copy (VSS) snapshot service. Creates shadow copies for read-only access to locked files.
/// Requires Administrator and running VSS service. Partial success: failed volumes fall back to live paths.
/// Snapshots are created per-volume (not a single point-in-time set); multiple volumes may differ slightly in time.
/// </summary>
public sealed class VssSnapshotService
{
    /// <summary>Pre-check: is the Volume Shadow Copy service (VSS) running?</summary>
    public static bool IsVssServiceRunning()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='VSS'");
            using var results = searcher.Get();
            var first = results.Cast<ManagementObject>().FirstOrDefault();
            return string.Equals(first?["State"]?.ToString(), "Running", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Pre-check: is the current process running as Administrator?</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public VssSnapshotContext? TryCreateContextForPaths(IEnumerable<string> paths, out string? warning)
    {
        warning = null;
        var warnings = new List<string>();

        if (!IsRunningAsAdmin())
            warnings.Add("Not running as Administrator; VSS requires elevation");

        if (!IsVssServiceRunning())
            warnings.Add("Volume Shadow Copy service (VSS) is not running; start the service or run as Administrator");

        var volumes = new List<string>();
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(root))
                    volumes.Add(root);
            }
            catch
            {
                // Ignore invalid paths
            }
        }

        volumes = volumes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (volumes.Count == 0)
        {
            if (warnings.Count > 0)
                warning = string.Join(" | ", warnings);
            return null;
        }

        var snapshots = new Dictionary<string, VssSnapshot>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();

        foreach (var volume in volumes)
        {
            var snapshot = TryCreateSnapshot(volume, out var error);
            if (snapshot != null)
                snapshots[volume] = snapshot;
            else
                failed.Add($"{volume} ({error})");
        }

        if (failed.Count > 0)
            warnings.Add($"VSS snapshot failed: {string.Join("; ", failed)}");

        if (snapshots.Count == 0)
        {
            warning = warnings.Count > 0 ? string.Join(" | ", warnings) : null;
            return null;
        }

        if (snapshots.Count > 1)
            warnings.Add("Snapshots created per-volume; not a single point-in-time set (consistency may vary)");

        if (warnings.Count > 0)
            warning = string.Join(" | ", warnings);

        return new VssSnapshotContext(snapshots);
    }

    private static VssSnapshot? TryCreateSnapshot(string volumeRoot, out string error)
    {
        error = string.Empty;
        try
        {
            var normalized = EnsureTrailingBackslash(volumeRoot);
            using var shadowClass = new ManagementClass("Win32_ShadowCopy");
            using var inParams = shadowClass.GetMethodParameters("Create");
            inParams["Context"] = "ClientAccessible";
            inParams["Volume"] = normalized;
            using var outParams = shadowClass.InvokeMethod("Create", inParams, null);

            var returnValue = (uint)(outParams?["ReturnValue"] ?? 1u);
            if (returnValue != 0)
            {
                error = GetVssCreateErrorMessage(returnValue);
                return null;
            }

            var snapshotId = outParams?["ShadowID"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(snapshotId))
            {
                error = "Missing ShadowID";
                return null;
            }

            using var shadow = new ManagementObject($"Win32_ShadowCopy.ID=\"{snapshotId}\"");
            var deviceObject = shadow["DeviceObject"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(deviceObject))
            {
                error = "Missing DeviceObject";
                return null;
            }

            return new VssSnapshot(snapshotId, normalized, deviceObject);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string GetVssCreateErrorMessage(uint returnValue)
    {
        return returnValue switch
        {
            0 => "Success",
            1 => "Access denied (run as Administrator)",
            2 => "Invalid argument",
            3 => "Volume not found",
            4 => "Volume not supported for shadow copy",
            5 => "Unsupported shadow copy context",
            6 => "Insufficient storage",
            7 => "Volume in use",
            8 => "Maximum shadow copies reached",
            9 => "Another shadow copy operation in progress",
            10 => "Shadow copy provider vetoed",
            11 => "Shadow copy provider not registered",
            12 => "Shadow copy provider failure",
            _ => $"VSS error {returnValue} (0x{returnValue:X})"
        };
    }

    private static string EnsureTrailingBackslash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        return value.EndsWith("\\", StringComparison.Ordinal) ? value : value + "\\";
    }
}

public sealed class VssSnapshotContext : IDisposable
{
    private readonly Dictionary<string, VssSnapshot> _snapshots;
    private bool _disposed;

    /// <summary>UTC time when this snapshot context was created (single reference for offline consistency).</summary>
    public DateTime CreationTimeUtc { get; }

    internal VssSnapshotContext(Dictionary<string, VssSnapshot> snapshots)
    {
        _snapshots = snapshots ?? new Dictionary<string, VssSnapshot>(StringComparer.OrdinalIgnoreCase);
        CreationTimeUtc = DateTime.UtcNow;
    }

    /// <summary>Returns true if the given path is resolved from a VSS snapshot (not live).</summary>
    public bool HasSnapshotForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _disposed)
            return false;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(root) && _snapshots.ContainsKey(root);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resolves path to snapshot path if the volume has a snapshot; otherwise returns the original (live) path.</summary>
    public string ResolvePath(string originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath) || _disposed)
            return originalPath ?? string.Empty;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(originalPath);
        }
        catch
        {
            return originalPath;
        }

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return originalPath;

        if (!_snapshots.TryGetValue(root, out var snapshot))
            return originalPath;

        var relative = fullPath.Substring(root.Length).TrimStart('\\', '/');
        return string.IsNullOrEmpty(relative)
            ? snapshot.DeviceObject.TrimEnd('\\')
            : Path.Combine(snapshot.DeviceObject.TrimEnd('\\'), relative);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            foreach (var snapshot in _snapshots.Values)
            {
                try
                {
                    snapshot.Dispose();
                }
                catch
                {
                    // Ignore per-snapshot cleanup failures
                }
            }
            _snapshots.Clear();
        }
        finally
        {
            _disposed = true;
        }
    }
}

public sealed class VssSnapshot : IDisposable
{
    public string SnapshotId { get; }
    public string VolumeRoot { get; }
    public string DeviceObject { get; }

    private bool _disposed;

    public VssSnapshot(string snapshotId, string volumeRoot, string deviceObject)
    {
        SnapshotId = snapshotId ?? string.Empty;
        VolumeRoot = volumeRoot ?? string.Empty;
        DeviceObject = deviceObject ?? string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            if (!string.IsNullOrWhiteSpace(SnapshotId))
            {
                using var shadow = new ManagementObject($"Win32_ShadowCopy.ID=\"{SnapshotId}\"");
                shadow.InvokeMethod("Delete", null);
            }
        }
        catch
        {
            // Ignore snapshot cleanup failures (e.g. already deleted)
        }
        finally
        {
            _disposed = true;
        }
    }
}
