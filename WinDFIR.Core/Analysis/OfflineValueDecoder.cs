using System;
using System.Globalization;

namespace WinDFIR.Core.Analysis;

/// <summary>
/// Decodes registry value strings as surfaced by the offline hive provider, which renders value data as
/// space-separated hex byte strings (UTF-16LE for REG_SZ/EXPAND_SZ, little-endian for DWORD) when raw bytes
/// are present. These helpers turn that back into usable text/integers for cross-source comparison.
/// </summary>
internal static class OfflineValueDecoder
{
    /// <summary>Decodes a space-separated hex byte string as UTF-16LE text; returns the input unchanged if it is not hex.</summary>
    public static string DecodeUtf16(string raw)
    {
        var bytes = TryParseHexBytes(raw);
        if (bytes == null)
            return raw;
        var len = bytes.Length - (bytes.Length % 2);
        return len <= 0 ? string.Empty : System.Text.Encoding.Unicode.GetString(bytes, 0, len).TrimEnd('\0');
    }

    /// <summary>Decodes a DWORD value, accepting a decimal string or a hex-byte (little-endian) rendering.</summary>
    public static int? DecodeDword(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (int.TryParse(raw, out var d))
            return d;
        var bytes = TryParseHexBytes(raw);
        if (bytes != null && bytes.Length >= 4)
            return BitConverter.ToInt32(bytes, 0);
        return null;
    }

    private static byte[]? TryParseHexBytes(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var trimmed = s.Replace("...", string.Empty).Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return null; // not a hex byte string -> already readable text
        }
        return bytes;
    }
}
