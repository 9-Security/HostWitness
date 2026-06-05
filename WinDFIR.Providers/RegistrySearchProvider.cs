using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinDFIR.Core.Entities;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// Live registry query provider (transitional). Uses Microsoft.Win32.RegistryKey and P/Invoke RegQueryInfoKey
/// with SafeRegistryHandle (RegistryKey.Handle on .NET 8); no DangerousGetHandle. For forensic soundness,
/// prefer OfflineHiveRegistryProvider where offline hives or VSS are available.
/// See docs\TECH_DEBT.md §1, §5 and docs\LIMITATIONS.md §2 (Rootkit/API hooking).
/// </summary>
public class RegistryQuery
{
    public string Name { get; set; } = string.Empty;
    public RegistryHive Hive { get; set; }
    public string KeyPath { get; set; } = string.Empty;
    public string? ValueNamePattern { get; set; }
    public string? DataPattern { get; set; }
    public bool Recursive { get; set; } = false;
}

public class RegistrySearchProvider : IProvider
{
    public string Name => "Registry Search Provider";
    public event EventHandler<ActivityEvent>? EventProduced;
    private readonly List<RegistryQuery> _queries;

    public RegistrySearchProvider()
    {
        _queries = new List<RegistryQuery>();
    }

    public void AddQuery(RegistryQuery query)
    {
        _queries.Add(query);
    }

    /// <summary>Clears all queries. Use before re-adding defaults or custom queries.</summary>
    public void ClearQueries()
    {
        _queries.Clear();
    }

    public void AddDefaultQueries()
    {
        _queries.Add(new RegistryQuery
        {
            Name = "User Run Key",
            Hive = RegistryHive.CurrentUser,
            KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run",
        });
        
        _queries.Add(new RegistryQuery
        {
            Name = "System Run Key",
            Hive = RegistryHive.LocalMachine,
            KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        });

        _queries.Add(new RegistryQuery 
        { 
            Name = "Recent Docs MRU",
            Hive = RegistryHive.CurrentUser,
            KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
            Recursive = true
        });
        
        _queries.Add(new RegistryQuery 
        { 
            Name = "Run Dialog MRU",
            Hive = RegistryHive.CurrentUser,
            KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU"
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return RunQueriesAsync(_queries, cancellationToken);
    }

    /// <summary>Runs a given set of queries without modifying the provider's internal list. Used by UI to run default queries with optional overrides.</summary>
    public Task RunQueriesAsync(IEnumerable<RegistryQuery> queries, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            foreach (var query in queries)
            {
                if (cancellationToken.IsCancellationRequested) break;
                ExecuteQuery(query);
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private static Regex? BuildRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private void ExecuteQuery(RegistryQuery query)
    {
        try
        {
            using Microsoft.Win32.RegistryKey baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(query.Hive, RegistryView.Default);
            using Microsoft.Win32.RegistryKey? key = baseKey.OpenSubKey(query.KeyPath);

            if (key == null) return;

            var valueNameRegex = BuildRegex(query.ValueNamePattern);
            var dataRegex = BuildRegex(query.DataPattern);
            ScanKey(key, query, valueNameRegex, dataRegex);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Registry query failed ({query.Name}): {ex.Message}");
        }
    }

    private void ScanKey(Microsoft.Win32.RegistryKey key, RegistryQuery query, Regex? valueNameRegex, Regex? dataRegex)
    {
        try
        {
            DateTime keyLastWriteTime = GetKeyLastWriteTime(key);
            var mruOrder = GetMruOrder(key);
            var isRecentDocsKey = IsRecentDocsKey(query, key);
            var recentDocsExtension = GetRecentDocsExtension(key, query);

            foreach (var valueName in key.GetValueNames())
            {
                if (valueNameRegex != null)
                {
                    if (!valueNameRegex.IsMatch(valueName))
                        continue;
                }

                if (valueName.Equals("MRUListEx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var valueData = key.GetValue(valueName);
                if (valueData == null) continue;

                var valueString = valueData.ToString() ?? string.Empty;
                string? rawHex = null;
                int? mruIndex = null;

                RecentDocsParseResult? recentDocsResult = null;
                if (valueData is byte[] bytes)
                {
                    rawHex = BitConverter.ToString(bytes).Replace("-", " ");
                    if (rawHex.Length > 200) rawHex = rawHex.Substring(0, 200) + "...";

                    if (isRecentDocsKey)
                    {
                        recentDocsResult = RecentDocsParser.Parse(bytes);
                        valueString = string.IsNullOrWhiteSpace(recentDocsResult.ParsedPath) ? rawHex : recentDocsResult.ParsedPath;
                    }
                    else
                    {
                        valueString = rawHex;
                    }
                }

                if (dataRegex != null)
                {
                    if (!dataRegex.IsMatch(valueString))
                        continue;
                }

                string fullKeyPath = key.Name;

                var evt = new ActivityEvent
                {
                    Timestamp = keyLastWriteTime,
                    Action = "Query", // artifact-derived presence
                    Category = "Registry",
                    Summary = $"{query.Name}: {key.Name}\\{valueName}",
                    ObjectRegistry = new WinDFIR.Core.Entities.RegistryKey(fullKeyPath),
                    Evidence = new List<EvidenceRef> 
                    { 
                        new EvidenceRef("Registry", fullKeyPath, collectedAt: DateTime.UtcNow) 
                    },
                    Fields = new Dictionary<string, object>
                    {
                        { "ValueName", valueName },
                        { "ValueData", valueString },
                        { "ValueType", key.GetValueKind(valueName).ToString() },
                        { "QueryName", query.Name }
                    }
                };

                if (!string.IsNullOrWhiteSpace(rawHex))
                {
                    evt.Fields["RawHex"] = rawHex!;
                }

                if (isRecentDocsKey)
                {
                    evt.Fields["Parser"] = "RecentDocs";
                    if (!string.IsNullOrWhiteSpace(recentDocsExtension))
                    {
                        evt.Fields["RecentDocsExtension"] = recentDocsExtension!;
                    }

                    if (int.TryParse(valueName, out var mruId) && mruOrder.TryGetValue(mruId, out var order))
                    {
                        mruIndex = order;
                    }
                    if (mruIndex.HasValue)
                    {
                        evt.Fields["MruOrder"] = mruIndex.Value;
                    }

                    if (recentDocsResult != null)
                    {
                        evt.Fields["ParsedPath"] = recentDocsResult.ParsedPath;
                        evt.Fields["FileName"] = recentDocsResult.FileName;
                        evt.Fields["ShellItemCount"] = recentDocsResult.ShellItemCount;
                        evt.Fields["ShellItemTypes"] = recentDocsResult.ShellItemTypes;
                    }
                }
                
                EventProduced?.Invoke(this, evt);
            }

            if (query.Recursive)
            {
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using Microsoft.Win32.RegistryKey? subKey = key.OpenSubKey(subKeyName);
                        if (subKey != null)
                        {
                            ScanKey(subKey, query, valueNameRegex, dataRegex);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Registry subkey scan failed ({query.Name}\\{subKeyName}): {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Registry key scan failed ({query.Name}): {ex.Message}");
        }
    }

    #region P/Invoke (SafeHandle) — 過渡方案，鑑識請優先使用 Offline Hive

    // RegQueryInfoKey: SafeRegistryHandle only; no DangerousGetHandle.
    // RegistryKey.Handle is SafeRegistryHandle on .NET 8; do not Dispose the key while scanning (handle not duplicated).
    // Transitional provider — prefer OfflineHiveRegistryProvider (+ VSS) for forensic analysis.
    // Limitations and doc sync rules: docs\TECH_DEBT.md §1、§5；docs\LIMITATIONS.md §2（Rootkit/API hooking）；
    // docs\RegistrySearch說明.md。若變更下列 DllImport 宣告、參數型別或 handle 傳遞方式，須於同一變更中更新上述文件。

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey,
        IntPtr lpClass,
        IntPtr lpcbClass,
        IntPtr lpReserved,
        out uint lpcSubKeys,
        out uint lpcbMaxSubKeyLen,
        out uint lpcbMaxClassLen,
        out uint lpcValues,
        out uint lpcbMaxValueNameLen,
        out uint lpcbMaxValueLen,
        out uint lpcbSecurityDescriptor,
        out FILETIME lpftLastWriteTime);

    private DateTime GetKeyLastWriteTime(Microsoft.Win32.RegistryKey key)
    {
        try
        {
            uint subKeys, maxSubKeyLen, maxClassLen, values, maxValueNameLen, maxValueLen, securityDescriptor;
            FILETIME ft;

            int ret = RegQueryInfoKey(
                key.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                out subKeys,
                out maxSubKeyLen,
                out maxClassLen,
                out values,
                out maxValueNameLen,
                out maxValueLen,
                out securityDescriptor,
                out ft);

            if (ret == 0)
            {
                long fileTime = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
                return DateTime.FromFileTimeUtc(fileTime);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RegistrySearchProvider GetKeyLastWriteTime: {ex.Message}");
        }

        // Unknown last-write time. Return an explicit sentinel (epoch min, UTC) rather than DateTime.UtcNow:
        // fabricating the collection time would mis-date the registry artifact onto the live timeline and
        // create false correlations. A year-0001 timestamp is clearly flagged as "unknown" to the analyst.
        return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    }

    #endregion

    private static bool IsRecentDocsKey(RegistryQuery query, Microsoft.Win32.RegistryKey key)
    {
        if (query.Name.Contains("Recent Docs", StringComparison.OrdinalIgnoreCase))
            return true;

        return key.Name.Contains(@"\RecentDocs", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<int, int> GetMruOrder(Microsoft.Win32.RegistryKey key)
    {
        var order = new Dictionary<int, int>();
        try
        {
            if (key.GetValue("MRUListEx") is not byte[] bytes || bytes.Length < 4)
                return order;

            var index = 0;
            for (var i = 0; i + 4 <= bytes.Length; i += 4)
            {
                var id = BitConverter.ToInt32(bytes, i);
                if (id == -1)
                    break;
                order[id] = index++;
            }
        }
        catch
        {
            // ignore MRUListEx parsing errors
        }

        return order;
    }

    private static string GetRecentDocsExtension(Microsoft.Win32.RegistryKey key, RegistryQuery query)
    {
        if (!IsRecentDocsKey(query, key))
            return string.Empty;

        var name = key.Name;
        var marker = @"\RecentDocs\";
        var idx = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return string.Empty;

        var tail = name.Substring(idx + marker.Length);
        if (string.IsNullOrWhiteSpace(tail))
            return string.Empty;

        var parts = tail.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

}
