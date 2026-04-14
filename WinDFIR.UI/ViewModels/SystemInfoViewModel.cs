using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WinDFIR.UI.ViewModels;

public class SystemInfoItem
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class SystemInfoCategory
{
    public string Title { get; set; } = string.Empty;
    public ObservableCollection<SystemInfoItem> Items { get; } = new();

    public SystemInfoCategory(string title)
    {
        Title = title;
    }

    public void Add(string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Items.Add(new SystemInfoItem { Key = key, Value = value });
        }
    }
}

public class SystemInfoViewModel : BaseViewModel
{
    public ObservableCollection<SystemInfoCategory> Categories { get; } = new();
    private int _refreshing;
    private bool _refreshPending;

    public void Refresh()
    {
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _refreshPending = true;
            return;
        }

        try
        {
            do
            {
                _refreshPending = false;
                var categories = await Task.Run(BuildCategories);
                try
                {
                    if (Application.Current?.Dispatcher != null)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            Categories.Clear();
                            foreach (var category in categories)
                                Categories.Add(category);
                        });
                }
                catch (InvalidOperationException) { /* app shutting down */ }
            } while (_refreshPending);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private static List<SystemInfoCategory> BuildCategories()
    {
        var categories = new List<SystemInfoCategory>
        {
            LoadOSInfo(),
            LoadHardwareInfo(),
            LoadNetworkInfo(),
            LoadUserInfo()
        };

        return categories;
    }

    private static SystemInfoCategory LoadOSInfo()
    {
        var cat = new SystemInfoCategory("Operating System");
        
        try 
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
            using var collection = searcher.Get();
            var os = collection.Cast<ManagementObject>().FirstOrDefault();

            if (os != null)
            {
                cat.Add("Name", os["Caption"]?.ToString() ?? Environment.OSVersion.VersionString);
                cat.Add("Version", os["Version"]?.ToString() ?? string.Empty);
                cat.Add("Build Number", os["BuildNumber"]?.ToString() ?? string.Empty);
                cat.Add("Architecture", os["OSArchitecture"]?.ToString() ?? (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
                
                if (os["LastBootUpTime"] != null)
                {
                    try {
                        var bootTime = ManagementDateTimeConverter.ToDateTime(os["LastBootUpTime"].ToString());
                        cat.Add("Last Boot Time", bootTime.ToString("yyyy-MM-dd HH:mm:ss"));
                        var uptime = DateTime.Now - bootTime;
                        cat.Add("Uptime", $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m");
                    } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SystemInfoViewModel LastBootUpTime: {ex.Message}"); }
                }

                if (os["InstallDate"] != null)
                {
                    try {
                        var installDate = ManagementDateTimeConverter.ToDateTime(os["InstallDate"].ToString());
                        cat.Add("Install Date", installDate.ToString("yyyy-MM-dd HH:mm:ss"));
                    } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SystemInfoViewModel InstallDate: {ex.Message}"); }
                }
                
                cat.Add("System Directory", Environment.SystemDirectory);
            }
            else
            {
                // Fallback
                cat.Add("OS Version", Environment.OSVersion.ToString());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SystemInfoViewModel LoadOsInfo: {ex.Message}");
            cat.Add("OS Description", RuntimeInformation.OSDescription);
        }

        return cat;
    }

    private static SystemInfoCategory LoadHardwareInfo()
    {
        var cat = new SystemInfoCategory("Hardware / System");

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            using var collection = searcher.Get();
            var comp = collection.Cast<ManagementObject>().FirstOrDefault();

            if (comp != null)
            {
                cat.Add("Hostname", comp["DNSHostName"]?.ToString() ?? Environment.MachineName);
                cat.Add("Domain", comp["Domain"]?.ToString() ?? string.Empty);
                cat.Add("Manufacturer", comp["Manufacturer"]?.ToString() ?? string.Empty);
                cat.Add("Model", comp["Model"]?.ToString() ?? string.Empty);
                
                if (comp["TotalPhysicalMemory"] != null && long.TryParse(comp["TotalPhysicalMemory"].ToString(), out var ramBytes))
                {
                   cat.Add("Total RAM", $"{(ramBytes / 1024d / 1024d / 1024d):F2} GB");
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SystemInfoViewModel LoadHardwareInfo ComputerSystem: {ex.Message}"); }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            using var collection = searcher.Get();
            var cpu = collection.Cast<ManagementObject>().FirstOrDefault();
            if (cpu != null)
            {
                cat.Add("Processor", cpu["Name"]?.ToString() ?? string.Empty);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SystemInfoViewModel LoadHardwareInfo Processor: {ex.Message}"); }

        cat.Add("Processor Count", Environment.ProcessorCount.ToString());

        return cat;
    }

    private static SystemInfoCategory LoadNetworkInfo()
    {
        var cat = new SystemInfoCategory("Network Interfaces");

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            foreach (ManagementObject adapter in searcher.Get())
            {
                var desc = adapter["Description"]?.ToString() ?? "Unknown Adapter";
                var ips = adapter["IPAddress"] as string[];
                var mac = adapter["MACAddress"]?.ToString();

                if (ips != null && ips.Length > 0)
                {
                    var sb = new StringBuilder();
                    foreach(var ip in ips) sb.Append(ip).Append("; ");
                    
                    cat.Add(desc, $"{sb.ToString().TrimEnd(' ', ';')} ({mac})");
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SystemInfoViewModel LoadNetworkInfo: {ex.Message}"); }

        if (cat.Items.Count == 0)
        {
            cat.Add("Status", "No active interfaces found via WMI.");
        }

        return cat;
    }

    private static SystemInfoCategory LoadUserInfo()
    {
        var cat = new SystemInfoCategory("User & Security");

        cat.Add("Current User", Environment.UserName);
        cat.Add("User Domain", Environment.UserDomainName);
        cat.Add("Is Admin", IsAdministrator() ? "Yes" : "No");
        
        try
        {
            var timeZone = TimeZoneInfo.Local;
            cat.Add("Time Zone", timeZone.DisplayName);
            cat.Add("Local Time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cat.Add("UTC Time", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SystemInfoViewModel LoadUserInfo TimeZone: {ex.Message}"); }

        return cat;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SystemInfoViewModel IsAdministrator: {ex.Message}");
            return false;
        }
    }
}
