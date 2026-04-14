using System.Text;

namespace WinDFIR.Providers.Parsers;

public sealed class UserAssistDecoded
{
    public string RawValueName { get; init; } = string.Empty;
    public string DecodedName { get; init; } = string.Empty;
    public uint? FocusCount { get; init; }
    public uint RunCount { get; init; }
    public DateTime? LastExecutionUtc { get; init; }
}

/// <summary>
/// Decodes UserAssist Count values (ROT13 name + binary run metadata). Layouts vary slightly by OS;
/// parser uses conservative heuristics and degrades without throwing.
/// </summary>
public static class UserAssistParser
{
    public static bool TryDecode(string? valueName, byte[]? data, out UserAssistDecoded decoded)
    {
        if (string.IsNullOrEmpty(valueName) || data == null || data.Length < 8)
        {
            decoded = new UserAssistDecoded();
            return false;
        }

        var decodedName = Rot13(valueName);
        uint? focus = null;
        uint run = 0;
        DateTime? last = null;

        if (data.Length >= 16)
        {
            var fc = BitConverter.ToUInt32(data, 0);
            var rc = BitConverter.ToUInt32(data, 4);
            var ft = BitConverter.ToUInt64(data, 8);
            if (TryFileTimeUtc(ft, out var t1))
            {
                focus = fc;
                run = rc;
                last = t1;
            }
            else if (TryFileTimeUtc(BitConverter.ToUInt64(data, 4), out var t2) && data.Length >= 12)
            {
                run = BitConverter.ToUInt32(data, 0);
                last = t2;
            }
        }
        else if (data.Length >= 8)
        {
            var ftOnly = BitConverter.ToUInt64(data, 0);
            if (TryFileTimeUtc(ftOnly, out var t))
                last = t;
        }

        decoded = new UserAssistDecoded
        {
            RawValueName = valueName,
            DecodedName = decodedName,
            FocusCount = focus,
            RunCount = run,
            LastExecutionUtc = last
        };
        return true;
    }

    internal static string Rot13(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is >= 'a' and <= 'z')
                sb.Append((char)((c - 'a' + 13) % 26 + 'a'));
            else if (c is >= 'A' and <= 'Z')
                sb.Append((char)((c - 'A' + 13) % 26 + 'A'));
            else
                sb.Append(c);
        }
        return sb.ToString();
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
