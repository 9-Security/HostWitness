using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinDFIR.Core.IO;

/// <summary>
/// Enables SeBackupPrivilege and opens files with FILE_FLAG_BACKUP_SEMANTICS so that
/// NTFS metadata files (e.g. $MFT, $Boot) can be read even when the volume is "in use".
/// Requires Administrator. Used as the first strategy for MFT collection (before VSS / raw volume).
/// </summary>
internal static class BackupPrivilege
{
    internal readonly record struct BackupReadResult(byte[]? Bytes, bool WasTruncated, long? LogicalSizeBytes, string? FailureReason)
    {
        public int ReadLength => Bytes?.Length ?? 0;
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint FILE_SHARE_DELETE = 0x04;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private const string SE_BACKUP_NAME = "SeBackupPrivilege";
    private const int TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const int TOKEN_QUERY = 0x08;
    private const int SE_PRIVILEGE_ENABLED = 0x02;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    /// <summary>Enable SeBackupPrivilege for the current process. Returns true if enabled or already enabled. Requires Administrator.</summary>
    public static bool EnableBackupPrivilege()
    {
        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tokenHandle))
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, SE_BACKUP_NAME, out var luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };
            bool adjusted = AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            int lastError = Marshal.GetLastWin32Error();
            return DidAdjustTokenPrivilegesSucceed(adjusted, lastError);
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
                CloseHandle(tokenHandle);
        }
    }

    internal static bool DidAdjustTokenPrivilegesSucceed(bool adjusted, int lastError)
    {
        return adjusted && lastError != ERROR_NOT_ALL_ASSIGNED;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Read up to maxBytes from path using backup semantics. Returns null on failure. Path must be like \\.\C:\$MFT or \\.\C:\$Boot.</summary>
    public static byte[]? ReadFileWithBackupSemantics(string path, int maxBytes, out string? failureReason)
    {
        var result = ReadFileWithBackupSemanticsDetailed(path, maxBytes);
        failureReason = result.FailureReason;
        return result.Bytes;
    }

    internal static BackupReadResult ReadFileWithBackupSemanticsDetailed(string path, int maxBytes)
    {
        if (string.IsNullOrEmpty(path) || maxBytes <= 0)
            return new BackupReadResult(null, WasTruncated: false, LogicalSizeBytes: null, FailureReason: "Invalid path or size.");

        const uint flags = FILE_FLAG_BACKUP_SEMANTICS | FILE_ATTRIBUTE_NORMAL;
        var ptr = CreateFile(
            path,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            flags,
            IntPtr.Zero);
        if (ptr == INVALID_HANDLE_VALUE || ptr == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            return new BackupReadResult(null, WasTruncated: false, LogicalSizeBytes: null, FailureReason: $"CreateFile failed (0x{err:X}).");
        }

        using var handle = new SafeFileHandle(ptr, true);
        using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, false);
        long? logicalSizeBytes = TryGetStreamLength(stream);
        int bufferLength = logicalSizeBytes.HasValue
            ? (int)Math.Min(Math.Min((long)maxBytes, RawDiskReader.MftReadLimitBytes), logicalSizeBytes.Value)
            : Math.Min(maxBytes, RawDiskReader.MftReadLimitBytes);
        var buffer = new byte[bufferLength];
        int total = 0;
        try
        {
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
            }
        }
        catch (IOException ex)
        {
            return new BackupReadResult(null, WasTruncated: false, LogicalSizeBytes: logicalSizeBytes, FailureReason: ex.Message);
        }

        if (total == 0)
            return new BackupReadResult(null, WasTruncated: false, LogicalSizeBytes: logicalSizeBytes, FailureReason: "No data read.");

        if (total < buffer.Length)
            Array.Resize(ref buffer, total);

        bool wasTruncated = logicalSizeBytes.HasValue && logicalSizeBytes.Value > total;
        if (!wasTruncated && total == bufferLength)
        {
            try
            {
                wasTruncated = stream.ReadByte() != -1;
            }
            catch (IOException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        if (logicalSizeBytes.HasValue && logicalSizeBytes.Value < total)
            logicalSizeBytes = total;

        return new BackupReadResult(buffer, wasTruncated, logicalSizeBytes, FailureReason: null);
    }

    private static long? TryGetStreamLength(Stream stream)
    {
        try
        {
            return stream.Length;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
