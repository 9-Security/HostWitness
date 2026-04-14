using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Registry;
using Registry.Abstractions;

namespace WinDFIR.Providers.Parsers;

public class AmcacheEntry
{
    public string EntryType { get; set; } = string.Empty;
    public string KeyPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Sha1 { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? LastWriteTimeUtc { get; set; }
    public string InstallDateRaw { get; set; } = string.Empty;
}

public static class AmcacheParser
{
    private static bool _encodingRegistered;

    public static List<AmcacheEntry> Parse(string hivePath)
    {
        var entries = new List<AmcacheEntry>();
        if (!File.Exists(hivePath))
            return entries;

        if (!_encodingRegistered)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            _encodingRegistered = true;
        }

        var hive = new RegistryHive(hivePath);
        hive.ParseHive();
        var root = hive.Root;
        if (root == null)
            return entries;

        ParseSection(root, @"Root\InventoryApplication", "Application", entries);
        ParseSection(root, @"Root\InventoryApplicationFile", "File", entries);

        return entries;
    }

    private static void ParseSection(RegistryKey root, string path, string entryType, List<AmcacheEntry> entries)
    {
        var section = GetKey(root, path);
        if (section == null)
            return;

        foreach (var subKey in section.SubKeys)
        {
            var name = GetStringValue(subKey, "Name");
            var publisher = GetStringValue(subKey, "Publisher");
            var version = GetStringValue(subKey, "Version");
            var productName = GetStringValue(subKey, "ProductName");
            var fileId = GetStringValue(subKey, "FileId");
            if (string.IsNullOrWhiteSpace(fileId))
                fileId = GetStringValue(subKey, "ProgramId");

            var pathValue = GetStringValue(subKey, "LowerCaseLongPath");
            if (string.IsNullOrWhiteSpace(pathValue))
                pathValue = GetStringValue(subKey, "Path");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                var rootDir = GetStringValue(subKey, "RootDirPath");
                if (!string.IsNullOrWhiteSpace(rootDir) && !string.IsNullOrWhiteSpace(name)
                    && rootDir.IndexOf("..", StringComparison.Ordinal) < 0
                    && name.IndexOf("..", StringComparison.Ordinal) < 0)
                    pathValue = Path.Combine(rootDir, name);
            }

            var sha1 = GetHexValue(subKey, "SHA1");
            if (string.IsNullOrWhiteSpace(sha1))
                sha1 = GetHexValue(subKey, "Sha1");

            var fileSize = GetLongValue(subKey, "Size");
            if (fileSize == 0)
                fileSize = GetLongValue(subKey, "FileSize");

            var installDate = GetStringValue(subKey, "InstallDate");
            if (string.IsNullOrWhiteSpace(installDate))
                installDate = GetStringValue(subKey, "InstallDateUtc");

            if (string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(pathValue) &&
                string.IsNullOrWhiteSpace(sha1))
            {
                continue;
            }

            entries.Add(new AmcacheEntry
            {
                EntryType = entryType,
                KeyPath = subKey.KeyPath,
                Name = name,
                Publisher = publisher,
                Version = string.IsNullOrWhiteSpace(version) ? GetStringValue(subKey, "ProductVersion") : version,
                ProductName = productName,
                Path = pathValue,
                Sha1 = sha1,
                FileId = fileId,
                FileSize = fileSize,
                LastWriteTimeUtc = subKey.LastWriteTime?.UtcDateTime,
                InstallDateRaw = installDate
            });
        }
    }

    private static RegistryKey? GetKey(RegistryKey root, string path)
    {
        var parts = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var part in parts)
        {
            var next = current.SubKeys.FirstOrDefault(k =>
                k.KeyName.Equals(part, StringComparison.OrdinalIgnoreCase));

            if (next == null)
                return null;

            current = next;
        }

        return current;
    }

    private static string GetStringValue(RegistryKey key, string valueName)
    {
        var value = FindValue(key, valueName);
        if (value == null)
            return string.Empty;

        if (value.ValueDataRaw is byte[] bytes)
        {
            var text = value.ValueData?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text ?? string.Empty;

            return BitConverter.ToString(bytes).Replace("-", " ");
        }

        return value.ValueData?.ToString() ?? string.Empty;
    }

    private static string GetHexValue(RegistryKey key, string valueName)
    {
        var value = FindValue(key, valueName);
        if (value == null)
            return string.Empty;

        if (value.ValueDataRaw is byte[] bytes)
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        return value.ValueData?.ToString() ?? string.Empty;
    }

    private static long GetLongValue(RegistryKey key, string valueName)
    {
        var value = FindValue(key, valueName);
        if (value == null)
            return 0;

        object? rawValue = value.ValueData;
        if (rawValue is int intValue)
            return intValue;
        if (rawValue is long longValue)
            return longValue;
        if (rawValue is uint uintValue)
            return uintValue;

        if (value.ValueDataRaw is byte[] bytes)
        {
            if (bytes.Length == 4)
                return BitConverter.ToInt32(bytes, 0);
            if (bytes.Length >= 8)
                return BitConverter.ToInt64(bytes, 0);
        }

        var textValue = rawValue?.ToString();
        if (!string.IsNullOrWhiteSpace(textValue) &&
            long.TryParse(textValue, NumberStyles.Any, CultureInfo.InvariantCulture, out long parsed))
            return parsed;

        return 0;
    }

    private static dynamic? FindValue(RegistryKey key, string valueName)
    {
        return key.Values.FirstOrDefault(v =>
            v.ValueName.Equals(valueName, StringComparison.OrdinalIgnoreCase));
    }
}
