using System;
using System.Management;
using System.Threading;

namespace WinDFIR.Providers;

public static class BootIdProvider
{
    private static readonly Lazy<ulong> BootId = new(ComputeBootId, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ulong GetBootId() => BootId.Value;

    private static ulong ComputeBootId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SystemUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var uptimeSeconds = Convert.ToUInt64(obj["SystemUpTime"]);
                var bootTime = DateTime.UtcNow - TimeSpan.FromSeconds(uptimeSeconds);
                return (ulong)bootTime.ToFileTimeUtc();
            }
        }
        catch
        {
            // Fallback to TickCount64
        }

        var fallbackBoot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return (ulong)fallbackBoot.ToFileTimeUtc();
    }
}
