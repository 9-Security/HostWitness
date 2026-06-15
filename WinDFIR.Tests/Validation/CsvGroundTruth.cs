using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinDFIR.Tests.Validation;

/// <summary>
/// Minimal CSV reader for ground-truth files produced by reference tools (SrumECmd, JLECmd, …). Handles
/// double-quoted fields with embedded commas and a leading UTF-8 BOM. Returns rows keyed by header name.
/// </summary>
internal static class CsvGroundTruth
{
    public static List<Dictionary<string, string>> Read(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        using var reader = new StreamReader(path, Encoding.UTF8);

        var headerLine = reader.ReadLine();
        if (headerLine == null)
            return rows;
        var headers = SplitCsvLine(headerLine);
        if (headers.Count > 0)
            headers[0] = headers[0].TrimStart('﻿'); // strip BOM on first column

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0)
                continue;
            var fields = SplitCsvLine(line);
            var row = new Dictionary<string, string>(headers.Count, System.StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count && i < fields.Count; i++)
                row[headers[i]] = fields[i];
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
