using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinDFIR.Providers.Parsers;

public class ShellItemInfo
{
    public byte Type { get; set; }
    public List<string> Strings { get; set; } = new();
}

public class RecentDocsParseResult
{
    public string ParsedPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ShellItemTypes { get; set; } = string.Empty;
    public int ShellItemCount { get; set; }
    public List<ShellItemInfo> Items { get; set; } = new();
}

public static class RecentDocsParser
{
    public static RecentDocsParseResult Parse(byte[] bytes)
    {
        var items = ParseItemIdList(bytes);
        var pathSegments = new List<string>();

        foreach (var item in items)
        {
            var candidates = item.Strings.Where(IsLikelyPathSegment).ToList();
            if (candidates.Count > 0)
            {
                pathSegments.Add(candidates.Last());
            }
        }

        var parsedPath = string.Join("\\", pathSegments.Where(s => !string.IsNullOrWhiteSpace(s)));
        var fileName = string.Empty;
        if (!string.IsNullOrWhiteSpace(parsedPath))
        {
            try
            {
                fileName = System.IO.Path.GetFileName(parsedPath);
            }
            catch
            {
                fileName = parsedPath;
            }
        }

        var types = string.Join(",", items.Select(i => $"0x{i.Type:X2}"));

        return new RecentDocsParseResult
        {
            ParsedPath = parsedPath,
            FileName = fileName,
            ShellItemCount = items.Count,
            ShellItemTypes = types,
            Items = items
        };
    }

    private static List<ShellItemInfo> ParseItemIdList(byte[] bytes)
    {
        var items = new List<ShellItemInfo>();
        var offset = 0;

        while (offset + 2 <= bytes.Length)
        {
            var size = BitConverter.ToUInt16(bytes, offset);
            if (size == 0)
            {
                break;
            }
            if (offset + size > bytes.Length || size < 2)
            {
                break;
            }

            var itemData = new byte[size - 2];
            Buffer.BlockCopy(bytes, offset + 2, itemData, 0, size - 2);

            var item = new ShellItemInfo
            {
                Type = itemData.Length > 0 ? itemData[0] : (byte)0,
                Strings = ExtractStrings(itemData)
            };

            items.Add(item);
            offset += size;
        }

        return items;
    }

    private static List<string> ExtractStrings(byte[] bytes)
    {
        var results = new List<string>();
        try
        {
            var unicode = Encoding.Unicode.GetString(bytes);
            results.AddRange(unicode.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            // ignore unicode errors
        }

        try
        {
            var ascii = Encoding.ASCII.GetString(bytes);
            results.AddRange(ascii.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            // ignore ascii errors
        }

        return results.Select(s => s.Trim())
            .Where(s => s.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsLikelyPathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Contains(@":\") || value.Contains(@"\") || value.Contains("/"))
            return true;

        return value.Contains('.') && value.Length >= 4;
    }
}
