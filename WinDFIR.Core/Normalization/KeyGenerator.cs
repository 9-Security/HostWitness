using WinDFIR.Core.Entities;

namespace WinDFIR.Core.Normalization;

/// <summary>
/// Centralized key generation utilities.
/// Ensures consistent entity identification across all providers.
/// </summary>
public static class KeyGenerator
{
    /// <summary>
    /// Generates ProcessKey from boot ID, PID, and creation time.
    /// Per specification: Process = (BootId, PID, CreateTime)
    /// </summary>
    public static ProcessKey GenerateProcessKey(ulong bootId, uint processId, DateTime createTime)
    {
        return new ProcessKey(bootId, processId, createTime);
    }

    /// <summary>
    /// Generates NetworkFlowKey from connection parameters.
    /// Per specification: NetworkFlow = (Proto, LocalEP, RemoteEP, PID, TimeBucket)
    /// </summary>
    public static NetworkFlowKey GenerateNetworkFlowKey(
        string protocol,
        string localEndpoint,
        string remoteEndpoint,
        uint? processId,
        DateTime timeBucket)
    {
        return new NetworkFlowKey(
            protocol ?? "UNKNOWN",
            localEndpoint ?? string.Empty,
            remoteEndpoint ?? string.Empty,
            processId,
            timeBucket);
    }

    /// <summary>
    /// Generates UserKey from Windows SID.
    /// Per specification: User = Windows SID
    /// </summary>
    public static UserKey GenerateUserKey(string sid)
    {
        return new UserKey(sid);
    }

    /// <summary>
    /// Generates FileKey from volume serial and file ID.
    /// Per specification: File = (VolumeSerial, FileId) or Path+Hash
    /// </summary>
    public static FileKey GenerateFileKey(string? volumeSerial, ulong? fileId, string? path, string? hash)
    {
        return new FileKey(volumeSerial, fileId, path, hash);
    }

    /// <summary>
    /// Generates RegistryKey from normalized registry path.
    /// Per specification: RegistryKey = Normalized Registry Path
    /// </summary>
    public static RegistryKey GenerateRegistryKey(string normalizedPath)
    {
        return new RegistryKey(normalizedPath);
    }

    /// <summary>
    /// Generates HostKey from machine SID and hostname.
    /// Per specification: Host = Machine SID / Hostname
    /// </summary>
    public static HostKey GenerateHostKey(string machineSid, string hostname)
    {
        return new HostKey(machineSid, hostname);
    }

    /// <summary>
    /// Legacy method for backward compatibility.
    /// </summary>
    [Obsolete("Use GenerateNetworkFlowKey instead")]
    public static NetworkKey GenerateNetworkKey(
        string localAddress,
        ushort localPort,
        string remoteAddress,
        ushort remotePort,
        string protocol)
    {
        return new NetworkKey(
            localAddress ?? string.Empty,
            localPort,
            remoteAddress ?? string.Empty,
            remotePort,
            protocol ?? "UNKNOWN");
    }
}
