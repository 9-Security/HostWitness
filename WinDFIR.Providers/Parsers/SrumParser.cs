using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace WinDFIR.Providers.Parsers;

/// <summary>One resolved entry from the SRUM <c>SruDbIdMapTable</c> (an app/service string or a user SID).</summary>
public sealed class SrumIdEntry
{
    public required int Index { get; init; }
    public required bool IsUserSid { get; init; }
    public required string Value { get; init; }
}

/// <summary>One row of a SRUM provider (extension) table, with AppId/UserId resolved to readable values.</summary>
public sealed class SrumRecord
{
    public required string ProviderName { get; init; }
    public required string ProviderGuid { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public string? App { get; init; }
    public string? UserSid { get; init; }
    public required IReadOnlyDictionary<string, object?> Fields { get; init; }
}

/// <summary>
/// Parses the System Resource Usage Monitor database (<c>SRUDB.dat</c>) via <see cref="EseDatabaseReader"/>
/// (the OS ESE engine). Resolves the per-row <c>AppId</c>/<c>UserId</c> integers through
/// <c>SruDbIdMapTable</c> into application strings and user SIDs, and yields the well-known provider tables
/// (network data usage, connectivity, application resource/energy usage, …).
/// </summary>
public static class SrumParser
{
    /// <summary>Friendly names for the well-known SRUM provider (extension) table GUIDs.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownProviders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["{973F5D5C-1D90-4944-BE8E-24B94231A174}"] = "Network Data Usage",
        ["{DD6636C4-8929-4683-974E-22C046A43763}"] = "Network Connectivity Usage",
        ["{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}"] = "Application Resource Usage",
        ["{D10CA2FE-6FCF-4F6D-848E-B2E99266FA86}"] = "Energy Usage",
        ["{FEE4E14F-02A9-4550-B5CE-5FA2DA202E37}"] = "Energy Usage (Long-Term)",
        ["{5C8CF1C7-7257-4F13-B223-970EF5939312}"] = "Application Timeline",
        ["{7ACBBAA3-D029-4BE4-9A7A-0885927F1D8F}"] = "vfu",
        ["{B6D82AF1-F780-4E17-8077-6CB9AD8A6FC4}"] = "SDP Volume Provider",
        ["{DA73FB89-2BEA-4DDC-86B8-6E048C6DA477}"] = "SDP CPU Provider",
    };

    private const string IdMapTable = "SruDbIdMapTable";
    private static readonly HashSet<string> RowKeyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "AutoIncId", "TimeStamp", "AppId", "UserId"
    };

    /// <summary>
    /// Parses all well-known SRUM provider tables from <paramref name="srudbPath"/>. Each provider table is
    /// limited to <paramref name="perTableCap"/> rows (0 = unbounded); <paramref name="truncatedProviders"/>
    /// receives the friendly names of any table that hit the cap.
    /// </summary>
    public static IEnumerable<SrumRecord> Parse(string srudbPath, int perTableCap, out List<string> truncatedProviders)
    {
        var truncated = new List<string>();
        truncatedProviders = truncated;
        return ParseIterator(srudbPath, perTableCap, truncated);
    }

    private static IEnumerable<SrumRecord> ParseIterator(string srudbPath, int perTableCap, List<string> truncated)
    {
        using var reader = EseDatabaseReader.Open(srudbPath);

        var idMap = BuildIdMap(reader);
        var tables = reader.GetTableNames();

        foreach (var table in tables)
        {
            if (!KnownProviders.TryGetValue(table, out var providerName))
                continue;

            var emitted = 0;
            foreach (var row in reader.ReadRows(table))
            {
                if (perTableCap > 0 && emitted >= perTableCap)
                {
                    truncated.Add(providerName);
                    break;
                }

                var record = BuildRecord(table, providerName, row, idMap);
                if (record != null)
                {
                    emitted++;
                    yield return record;
                }
            }
        }
    }

    /// <summary>Builds the IdIndex → resolved value map from <c>SruDbIdMapTable</c>.</summary>
    public static IReadOnlyDictionary<int, SrumIdEntry> BuildIdMap(EseDatabaseReader reader)
    {
        var map = new Dictionary<int, SrumIdEntry>();
        foreach (var row in reader.ReadRows(IdMapTable))
        {
            if (!TryGetInt(row, "IdIndex", out var index))
                continue;

            var idType = TryGetInt(row, "IdType", out var t) ? t : 0;
            var blob = row.TryGetValue("IdBlob", out var b) ? b as byte[] : null;

            var isUser = idType == 3;
            var value = isUser ? DecodeSid(blob) : DecodeAppString(blob);
            map[index] = new SrumIdEntry { Index = index, IsUserSid = isUser, Value = value };
        }
        return map;
    }

    private static SrumRecord? BuildRecord(string guid, string providerName, IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<int, SrumIdEntry> idMap)
    {
        if (row.TryGetValue("TimeStamp", out var tsObj) && tsObj is DateTime ts)
        {
            string? app = null;
            string? userSid = null;
            if (TryGetInt(row, "AppId", out var appId) && idMap.TryGetValue(appId, out var appEntry))
                app = appEntry.Value;
            if (TryGetInt(row, "UserId", out var userId) && idMap.TryGetValue(userId, out var userEntry))
                userSid = userEntry.IsUserSid ? userEntry.Value : userEntry.Value;

            var fields = row
                .Where(kv => !RowKeyColumns.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            return new SrumRecord
            {
                ProviderName = providerName,
                ProviderGuid = guid,
                TimestampUtc = DateTime.SpecifyKind(ts, DateTimeKind.Utc),
                App = app,
                UserSid = userSid,
                Fields = fields
            };
        }

        return null;
    }

    private static string DecodeAppString(byte[]? blob)
    {
        if (blob == null || blob.Length == 0)
            return string.Empty;
        // App identifiers are stored as UTF-16LE strings (service names carry a "!!" prefix).
        var s = Encoding.Unicode.GetString(blob);
        var nul = s.IndexOf('\0');
        if (nul >= 0)
            s = s[..nul];
        return s;
    }

    private static string DecodeSid(byte[]? blob)
    {
        if (blob == null || blob.Length < 8)
            return string.Empty;
        try
        {
            return new SecurityIdentifier(blob, 0).Value;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, object?> row, string key, out int value)
    {
        value = 0;
        if (!row.TryGetValue(key, out var obj) || obj == null)
            return false;
        try
        {
            value = Convert.ToInt32(obj);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
