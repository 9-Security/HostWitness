using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WinDFIR.Providers.Parsers;

/// <summary>One BITS record (a queued download job or one of its files) recovered from <c>qmgr.db</c>.</summary>
public sealed class BitsRecord
{
    public required string Kind { get; init; }       // "File" or "Job"
    public Guid Id { get; init; }
    public IReadOnlyList<string> Urls { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> LocalPaths { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> OtherStrings { get; init; } = System.Array.Empty<string>();
}

/// <summary>
/// Recovers BITS (Background Intelligent Transfer Service) queue entries from <c>qmgr.db</c> (an ESE database).
/// </summary>
/// <remarks>
/// In the modern <c>qmgr.db</c> format each row of the <c>Jobs</c>/<c>Files</c> tables stores its data in a
/// single binary blob whose internal layout is undocumented and version-dependent. Rather than guess struct
/// offsets (which would risk fabricating fields), this parser performs <b>conservative UTF-16 string
/// extraction</b>: it pulls the readable strings out of each blob and classifies them as URLs, local/UNC paths,
/// or other. That surfaces the forensic gold — what was downloaded from where — without asserting structure it
/// cannot verify. Byte counts, states, and per-file ordering are intentionally not decoded.
/// </remarks>
public static class BitsParser
{
    private const string FilesTable = "Files";
    private const string JobsTable = "Jobs";
    private const string BlobColumn = "Blob";
    private const string IdColumn = "Id";
    private const int MinStringLength = 4;

    private static readonly Regex UrlPattern = new(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://", RegexOptions.Compiled);
    private static readonly Regex DrivePathPattern = new(@"^[A-Za-z]:\\", RegexOptions.Compiled);

    /// <summary>Parses the Jobs and Files tables of <paramref name="qmgrPath"/> into <see cref="BitsRecord"/>s.</summary>
    public static IEnumerable<BitsRecord> Parse(string qmgrPath)
    {
        using var reader = EseDatabaseReader.Open(qmgrPath);

        foreach (var record in ReadTable(reader, FilesTable, "File"))
            yield return record;
        foreach (var record in ReadTable(reader, JobsTable, "Job"))
            yield return record;
    }

    private static IEnumerable<BitsRecord> ReadTable(EseDatabaseReader reader, string table, string kind)
    {
        foreach (var row in reader.ReadRows(table))
        {
            var blob = row.TryGetValue(BlobColumn, out var b) ? b as byte[] : null;
            var id = row.TryGetValue(IdColumn, out var idObj) && idObj is Guid g ? g : Guid.Empty;

            var strings = blob != null ? ExtractUtf16Strings(blob) : new List<string>();
            yield return BuildRecord(kind, id, strings);
        }
    }

    internal static BitsRecord BuildRecord(string kind, Guid id, IReadOnlyList<string> strings)
    {
        var urls = new List<string>();
        var paths = new List<string>();
        var others = new List<string>();

        foreach (var s in strings)
        {
            if (UrlPattern.IsMatch(s))
                urls.Add(s);
            else if (DrivePathPattern.IsMatch(s) || s.StartsWith(@"\\", System.StringComparison.Ordinal))
                paths.Add(s);
            else if (IsLikelyText(s))
                others.Add(s); // job names, owner SIDs, etc. — drop binary-as-UTF16 noise
        }

        return new BitsRecord
        {
            Kind = kind,
            Id = id,
            Urls = Dedupe(urls),
            LocalPaths = Dedupe(paths),
            OtherStrings = Dedupe(others)
        };
    }

    /// <summary>
    /// Extracts readable UTF-16LE strings from a blob: runs of printable characters (length ≥ 4) bounded by
    /// non-printable/NUL units. Order is preserved; this makes no assumption about the blob's structure.
    /// </summary>
    internal static List<string> ExtractUtf16Strings(byte[] blob)
    {
        var results = new List<string>();
        var sb = new StringBuilder();

        for (var i = 0; i + 1 < blob.Length; i += 2)
        {
            var unit = (char)(blob[i] | (blob[i + 1] << 8));
            if (IsPrintable(unit))
            {
                sb.Append(unit);
            }
            else
            {
                Flush(sb, results);
            }
        }
        Flush(sb, results);

        return results;
    }

    private static void Flush(StringBuilder sb, List<string> results)
    {
        if (sb.Length >= MinStringLength)
            results.Add(sb.ToString());
        sb.Clear();
    }

    /// <summary>True if the string is mostly printable ASCII — keeps job names/SIDs, drops binary-as-UTF16 (CJK) noise.</summary>
    private static bool IsLikelyText(string s)
    {
        if (s.Length < MinStringLength)
            return false;
        var ascii = s.Count(ch => ch >= 0x20 && ch <= 0x7E);
        return ascii >= s.Length * 0.8;
    }

    private static bool IsPrintable(char c)
    {
        // Printable BMP range excluding control chars and surrogates (lone surrogates are binary noise, not text).
        if (c < 0x20 || c == 0x7F)
            return false;
        if (c >= 0xD800 && c <= 0xDFFF)
            return false;
        return c < 0xFFFE;
    }

    private static IReadOnlyList<string> Dedupe(List<string> items)
    {
        if (items.Count <= 1)
            return items;
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(items.Count);
        foreach (var item in items)
        {
            if (seen.Add(item))
                result.Add(item);
        }
        return result;
    }
}
