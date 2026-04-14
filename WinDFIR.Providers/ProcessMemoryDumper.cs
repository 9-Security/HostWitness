using System.IO;
using System.Runtime.InteropServices;

namespace WinDFIR.Providers;

/// <summary>
/// Writes a minidump of a process by PID using MiniDumpWriteDump (DbgHelp).
/// Requires PROCESS_QUERY_INFORMATION and PROCESS_VM_READ; typically needs Administrator for other processes.
/// </summary>
public static class ProcessMemoryDumper
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessDupHandle = 0x0040;
    private static readonly IntPtr InvalidHandle = new(-1);

    private const int MiniDumpNormal = 0;
    private const int MiniDumpWithFullMemory = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("Dbghelp.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        int dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    /// <summary>
    /// Writes a minidump of the process identified by <paramref name="processId"/> to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="processId">Target process ID.</param>
    /// <param name="outputPath">Full path for the .dmp file.</param>
    /// <param name="fullMemory">If true, use MiniDumpWithFullMemory (large file); otherwise MiniDumpNormal.</param>
    /// <returns>(true, null) on success; (false, errorMessage) on failure.</returns>
    public static (bool Success, string? ErrorMessage) DumpProcess(int processId, string outputPath, bool fullMemory = false)
    {
        if (processId <= 0)
            return (false, "Invalid process ID.");

        if (string.IsNullOrWhiteSpace(outputPath))
            return (false, "Output path is required.");

        var access = ProcessQueryInformation | ProcessVmRead | ProcessDupHandle;
        var hProcess = OpenProcess(access, false, processId);
        if (hProcess == IntPtr.Zero || hProcess == InvalidHandle)
        {
            var err = Marshal.GetLastWin32Error();
            return (false, $"OpenProcess failed (error {err}). Run as Administrator to dump other processes.");
        }

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var dumpType = fullMemory ? MiniDumpWithFullMemory : MiniDumpNormal;
            var hFile = fs.SafeFileHandle.DangerousGetHandle();
            var ok = MiniDumpWriteDump(hProcess, (uint)processId, hFile, dumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                return (false, $"MiniDumpWriteDump failed (error {err}).");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return (true, null);
    }
}
