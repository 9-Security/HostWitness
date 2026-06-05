using System.Globalization;

namespace WinDFIR.Providers.Parsers;

/// <summary>
/// Conservative structured fields for high-value offline persistence-related registry queries.
/// Mutates <paramref name="fields"/> and <paramref name="summary"/> in place; never throws.
/// </summary>
public static class OfflineRegistryPersistenceEnrichment
{
    public static void Apply(
        string queryName,
        string keyPathRelative,
        string valueName,
        string valueDataDisplay,
        byte[]? valueRaw,
        Dictionary<string, object> fields,
        ref string summary)
    {
        try
        {
            if (queryName.Equals("Services", StringComparison.OrdinalIgnoreCase))
                ApplyServices(keyPathRelative, valueName, valueDataDisplay, fields, ref summary);
            else if (queryName.Equals("StartupApprovedRun", StringComparison.OrdinalIgnoreCase))
                ApplyStartupApproved(valueName, valueRaw, fields, ref summary);
            else if (queryName.Equals("IFEO", StringComparison.OrdinalIgnoreCase))
                ApplyIfeo(keyPathRelative, valueName, valueDataDisplay, fields, ref summary);
            else if (queryName.Equals("Winlogon", StringComparison.OrdinalIgnoreCase))
                ApplyWinlogon(valueName, valueDataDisplay, fields, ref summary);
            else if (queryName.Equals("BitsClient", StringComparison.OrdinalIgnoreCase))
                ApplyBitsClient(keyPathRelative, valueName, valueDataDisplay, fields, ref summary);
            else if (queryName.Equals("WmiCimom", StringComparison.OrdinalIgnoreCase))
                ApplyWmiCimom(valueName, valueDataDisplay, fields, ref summary);
            else if (queryName.Equals("WmiNamespaceSecurity", StringComparison.OrdinalIgnoreCase))
                ApplyWmiNamespaceSecurity(keyPathRelative, valueName, valueDataDisplay, fields, ref summary);
            else if (queryName.Equals("SrumRegistry", StringComparison.OrdinalIgnoreCase))
                ApplySrumRegistry(keyPathRelative, valueName, valueDataDisplay, fields, ref summary);
        }
        catch
        {
            // Enrichment is optional; raw fields remain.
        }
    }

    private static void ApplyServices(
        string keyPathRelative,
        string valueName,
        string valueDataDisplay,
        Dictionary<string, object> fields,
        ref string summary)
    {
        if (!TryGetServiceContext(keyPathRelative, out var serviceName, out var controlSet))
            return;

        fields["OfflineHiveDecoded"] = "Services";
        fields["ServiceName"] = serviceName;
        if (!string.IsNullOrEmpty(controlSet))
            fields["ServiceControlSet"] = controlSet;

        var vn = valueName;
        var vd = valueDataDisplay.Trim();

        switch (vn.ToUpperInvariant())
        {
            case "IMAGEPATH":
                fields["ServiceImagePath"] = vd;
                break;
            case "DISPLAYNAME":
                fields["ServiceDisplayName"] = vd;
                break;
            case "DESCRIPTION":
                fields["ServiceDescription"] = vd;
                break;
            case "START":
                fields["ServiceStartType"] = vd;
                if (uint.TryParse(vd, NumberStyles.Integer, CultureInfo.InvariantCulture, out var st))
                    fields["ServiceStartTypeLabel"] = MapStartType(st);
                break;
            case "TYPE":
                fields["ServiceType"] = vd;
                if (uint.TryParse(vd, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tt))
                    fields["ServiceTypeLabel"] = MapServiceType(tt);
                break;
            case "OBJECTNAME":
                fields["ServiceObjectName"] = vd;
                break;
            case "GROUP":
                fields["ServiceGroup"] = vd;
                break;
            case "ERRORCONTROL":
                fields["ServiceErrorControl"] = vd;
                break;
            case "FAILURECOMMAND":
                fields["ServiceFailureCommand"] = vd;
                break;
            case "REBOOTMESSAGE":
                fields["ServiceRebootMessage"] = vd;
                break;
            case "SERVICEDLL":
                fields["ServiceDll"] = vd;
                break;
            default:
                fields.Remove("OfflineHiveDecoded");
                if (fields.ContainsKey("ServiceName"))
                    fields.Remove("ServiceName");
                if (fields.ContainsKey("ServiceControlSet"))
                    fields.Remove("ServiceControlSet");
                return;
        }

        var shortVal = vd.Length > 80 ? vd[..80] + "…" : vd;
        summary = $"Services [{serviceName}] {vn}: {shortVal}";
    }

    private static bool TryGetServiceContext(string keyPathRelative, out string serviceName, out string controlSet)
    {
        serviceName = string.Empty;
        controlSet = string.Empty;
        var parts = keyPathRelative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase))
                controlSet = p;
        }

        var si = Array.FindIndex(parts, x => x.Equals("Services", StringComparison.OrdinalIgnoreCase));
        if (si < 0 || si + 1 >= parts.Length)
            return false;
        var svc = parts[si + 1];
        if (svc.Equals("Parameters", StringComparison.OrdinalIgnoreCase))
            return false;
        serviceName = svc;
        return true;
    }

    private static string MapStartType(uint v) => v switch
    {
        0 => "Boot",
        1 => "System",
        2 => "Auto",
        3 => "Manual",
        4 => "Disabled",
        _ => "Other"
    };

    private static string MapServiceType(uint v) => v switch {
        0x1 => "KernelDriver",
        0x2 => "FileSystemDriver",
        0x10 => "OwnProcess",
        0x20 => "ShareProcess",
        _ => "Other"
    };

    private static void ApplyStartupApproved(
        string valueName,
        byte[]? valueRaw,
        Dictionary<string, object> fields,
        ref string summary)
    {
        if (valueRaw == null || valueRaw.Length == 0)
            return;

        fields["OfflineHiveDecoded"] = "StartupApprovedRun";
        fields["StartupApproved_EntryName"] = valueName;
        fields["StartupApproved_FirstByte"] = valueRaw[0];
        if (valueRaw.Length >= 8)
            fields["StartupApproved_HeaderPrefixHex"] = Convert.ToHexString(valueRaw.AsSpan(0, Math.Min(8, valueRaw.Length)));

        var state = ClassifyStartupApproved(valueRaw);
        fields["StartupApproved_State"] = state;

        // The standard StartupApproved value is 12 bytes: a 4-byte state flag followed by the 8-byte
        // FILETIME of when the entry was enabled/disabled. The previous ">= 16" guard skipped this
        // timestamp for virtually all real entries; read it at its fixed offset (4) instead.
        if (valueRaw.Length >= 12)
        {
            var ft = BitConverter.ToUInt64(valueRaw, 4);
            if (TryFileTimeUtc(ft, out var ts))
                fields["StartupApproved_LastWriteHintUtc"] = ts.ToString("o", CultureInfo.InvariantCulture);
        }

        summary = $"StartupApproved [{state}]: {valueName}";
    }

    /// <summary>Heuristic only; many builds use0x02/0x03 for disabled/enabled in Run/StartupApproved.</summary>
    private static string ClassifyStartupApproved(byte[] raw)
    {
        var b = raw[0];
        return b switch
        {
            0x02 => "Disabled",
            0x03 => "Enabled",
            _ => "Unknown"
        };
    }

    private static void ApplyIfeo(
        string keyPathRelative,
        string valueName,
        string valueDataDisplay,
        Dictionary<string, object> fields,
        ref string summary)
    {
        var target = GetLastPathSegment(keyPathRelative);
        if (string.IsNullOrEmpty(target))
            return;

        var vn = valueName;
        var vd = valueDataDisplay.Trim();
        if (string.IsNullOrEmpty(vd))
            return;

        fields["OfflineHiveDecoded"] = "IFEO";
        fields["IFEO_TargetImage"] = target;

        switch (vn.ToUpperInvariant())
        {
            case "DEBUGGER":
                fields["IFEO_Debugger"] = vd;
                summary = $"IFEO [{target}] Debugger configured";
                break;
            case "GLOBALFLAG":
                fields["IFEO_GlobalFlag"] = vd;
                summary = $"IFEO [{target}] GlobalFlag={vd}";
                break;
            case "MITIGATIONOPTIONS":
                fields["IFEO_MitigationOptions"] = vd;
                summary = $"IFEO [{target}] MitigationOptions";
                break;
            case "VERIFIERFLAGS":
                fields["IFEO_VerifierFlags"] = vd;
                summary = $"IFEO [{target}] VerifierFlags";
                break;
            case "FILTERFULLPATH":
                fields["IFEO_FilterFullPath"] = vd;
                summary = $"IFEO [{target}] FilterFullPath";
                break;
            default:
                fields.Remove("OfflineHiveDecoded");
                fields.Remove("IFEO_TargetImage");
                return;
        }
    }

    /// <summary>
    /// Offline SOFTWARE hive BITS client registry area (job metadata only; not a full BITS queue decode).
    /// </summary>
    private static void ApplyBitsClient(
        string keyPathRelative,
        string valueName,
        string valueDataDisplay,
        Dictionary<string, object> fields,
        ref string summary)
    {
        fields["OfflineArtifactFamily"] = "BITS";
        fields["OfflineHiveDecoded"] = "BITS_Registry";
        fields["Bits_KeyPathRelative"] = keyPathRelative;
        fields["Bits_ValueName"] = valueName;
        fields["Bits_Note"] = "Offline registry snapshot only; does not prove transfers or execution.";
        var vd = Truncate(valueDataDisplay.Trim(), 200);
        if (!string.IsNullOrEmpty(vd))
            fields["Bits_ValuePreview"] = vd;
        summary = $"BITS registry: {keyPathRelative}\\{valueName}";
    }

    /// <summary>
    /// WBEM CIMOM settings (e.g. Autorecover lists). Indicates repository maintenance configuration, not active subscriptions.
    /// </summary>
    private static void ApplyWmiCimom(
        string valueName,
        string valueDataDisplay,
        Dictionary<string, object> fields,
        ref string summary)
    {
        if (string.IsNullOrEmpty(valueName))
            return;

        fields["OfflineArtifactFamily"] = "WMI";
        fields["OfflineHiveDecoded"] = "WMI_Cimom";
        fields["Wmi_ValueName"] = valueName;
        var vd = Truncate(valueDataDisplay.Trim(), 240);
        if (!string.IsNullOrEmpty(vd))
            fields["Wmi_ValuePreview"] = vd;
        fields["Wmi_Note"] = "CIMOM registry values only; does not enumerate __EventFilter/Consumer bindings.";
        summary = string.IsNullOrEmpty(vd)
            ? $"WMI CIMOM: {valueName}"
            : $"WMI CIMOM: {valueName} = {Truncate(vd, 80)}";
    }

    /// <summary>
    /// SYSTEM hive ControlSet...\Control\WMI\Security subtree: per-namespace security descriptor storage (offline presence only).
    /// </summary>
    private static void ApplyWmiNamespaceSecurity(
        string keyPathRelative,
        string valueName,
        string valueDataDisplay,
        Dictionary<string, object> fields,
        ref string summary)
    {
        var ns = GetLastPathSegment(keyPathRelative);
        if (string.IsNullOrEmpty(ns))
            return;

        fields["OfflineArtifactFamily"] = "WMI";
        fields["OfflineHiveDecoded"] = "WMI_NamespaceSecurity";
        fields["Wmi_SecurityNamespaceKey"] = ns;
        fields["Wmi_ValueName"] = valueName;
        var vd = Truncate(valueDataDisplay.Trim(), 120);
        if (!string.IsNullOrEmpty(vd))
            fields["Wmi_ValuePreview"] = vd;
        fields["Wmi_Note"] = "Namespace security key present offline; not proof of malicious WMI persistence.";
        summary = $"WMI Security [{ns}] {valueName}";
    }

    /// <summary>
    /// SRUM-related SOFTWARE keys when present. SRUDB.dat ESE tables are not parsed here.
    /// </summary>
    private static void ApplySrumRegistry(
        string keyPathRelative,
        string valueName,
        string valueDataDisplay,
        Dictionary<string, object> fields,
        ref string summary)
    {
        fields["OfflineArtifactFamily"] = "SRUM";
        fields["OfflineHiveDecoded"] = "SRUM_Registry";
        fields["Srum_KeyPathRelative"] = keyPathRelative;
        fields["Srum_ValueName"] = valueName;
        var vd = Truncate(valueDataDisplay.Trim(), 200);
        if (!string.IsNullOrEmpty(vd))
            fields["Srum_ValuePreview"] = vd;
        fields["Srum_Note"] = "Registry slice only; SRUDB.dat / ESE execution history not decoded.";
        summary = $"SRUM registry: {keyPathRelative}\\{valueName}";
    }

    private static void ApplyWinlogon(
        string valueName,
        string valueDataDisplay,
        Dictionary<string, object> fields,
        ref string summary)
    {
        var vn = valueName;
        var vd = valueDataDisplay.Trim();
        if (string.IsNullOrEmpty(vd))
            return;

        string? fieldKey = vn.ToUpperInvariant() switch
        {
            "SHELL" => "Winlogon_Shell",
            "USERINIT" => "Winlogon_Userinit",
            "NOTIFY" => "Winlogon_Notify",
            "VMAPPLET" => "Winlogon_VmApplet",
            "AUTOADMINLOGON" => "Winlogon_AutoAdminLogon",
            "DEFAULTUSERNAME" => "Winlogon_DefaultUserName",
            "DEFAULTDOMAINNAME" => "Winlogon_DefaultDomainName",
            "LEGALNOTICECAPTION" => "Winlogon_LegalNoticeCaption",
            "LEGALNOTICETEXT" => "Winlogon_LegalNoticeText",
            "SYSTEM" => "Winlogon_System",
            _ => null
        };

        if (fieldKey == null)
            return;

        fields["OfflineHiveDecoded"] = "Winlogon";
        fields[fieldKey] = vd;
        summary = $"Winlogon {vn}: {Truncate(vd, 100)}";
    }

    private static string GetLastPathSegment(string keyPathRelative)
    {
        var parts = keyPathRelative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : parts[^1];
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s[..max] + "…";
    }

    private static bool TryFileTimeUtc(ulong fileTime, out DateTime utc)
    {
        utc = default;
        if (fileTime == 0)
            return false;
        try
        {
            utc = DateTime.FromFileTimeUtc((long)fileTime);
            if (utc.Year < 1990 || utc.Year > 2038)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
