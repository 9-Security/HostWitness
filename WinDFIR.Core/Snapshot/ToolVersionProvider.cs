using System;
using System.Reflection;

namespace WinDFIR.Core.Snapshot;

public static class ToolVersionProvider
{
    public static string GetCurrentVersion(Type anchorType)
    {
        ArgumentNullException.ThrowIfNull(anchorType);
        return GetCurrentVersion(anchorType.Assembly);
    }

    public static string GetCurrentVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var rawVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";

        return NormalizeVersion(rawVersion);
    }

    private static string NormalizeVersion(string rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
            return "Unknown";

        var trimmed = rawVersion.Split('+', 2, StringSplitOptions.TrimEntries)[0];
        if (!Version.TryParse(trimmed, out var version))
            return trimmed;

        if (version.Revision > 0)
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

        if (version.Build >= 0)
            return $"{version.Major}.{version.Minor}.{version.Build}";

        return $"{version.Major}.{version.Minor}";
    }
}