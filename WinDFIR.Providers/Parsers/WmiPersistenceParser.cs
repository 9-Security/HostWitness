using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinDFIR.Providers.Parsers;

/// <summary>One WMI persistence artifact recovered from the CIM repository: a filter, a consumer, or a binding.</summary>
public sealed class WmiPersistenceRecord
{
    public required string Kind { get; init; }   // "Filter" | "Consumer" | "Binding"
    public string? Name { get; init; }
    public string? ConsumerClass { get; init; }
    public string? ConsumerName { get; init; }
    public string? FilterName { get; init; }
    public string? Namespace { get; init; }
    public string? Query { get; init; }
    public string? QueryLanguage { get; init; }
}

/// <summary>
/// Triage-level recovery of WMI event-subscription persistence from <c>OBJECTS.DATA</c> (the CIM repository).
/// </summary>
/// <remarks>
/// Fully parsing the CIM repository (classes + instances via INDEX.BTR/MAPPING) is large and version-sensitive,
/// and mis-parsing it risks fabricating findings. Instead — like the well-known PyWMIPersistenceFinder triage —
/// this extracts the readable ASCII runs and matches the <b>instance</b> patterns that indicate an actual
/// subscription: <c>__EventFilter</c> instances carrying a WQL query, consumer instances written as
/// <c>&lt;Class&gt;EventConsumer.Name="…"</c>, and <c>__FilterToConsumerBinding</c> instances tying a consumer to
/// a filter. Class definitions / provider registrations (which list property declarations but no
/// <c>.Name="…"</c> values or query text) are deliberately not reported, so the output is the subscription
/// picture, not schema noise. The consumer's command-line/script <i>payload</i> is NOT decoded here (that needs a
/// real CIM parse, e.g. python-cim) — verify a suspicious consumer's action against the live repository.
/// </remarks>
public static class WmiPersistenceParser
{
    private const int MinRunLength = 3;
    private const int LookaheadRuns = 10;

    private static readonly Regex ConsumerInstance = new(@"^(\w*EventConsumer)\.Name=""(.+)""$", RegexOptions.Compiled);
    private static readonly Regex FilterRef = new(@"^__EventFilter\.Name=""(.+)""$", RegexOptions.Compiled);
    private static readonly Regex WqlQuery = new(@"\bselect\b.+\bfrom\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamespaceLike = new(@"^(root\\|ROOT\\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<WmiPersistenceRecord> Parse(string objectsDataPath)
    {
        var bytes = File.ReadAllBytes(objectsDataPath);
        return ExtractFromRuns(ExtractAsciiRuns(bytes));
    }

    internal static List<WmiPersistenceRecord> ExtractFromRuns(IReadOnlyList<string> runs)
    {
        var records = new List<WmiPersistenceRecord>();
        var seenBindings = new HashSet<string>();
        var seenFilters = new HashSet<string>();
        var seenConsumers = new HashSet<string>();

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];

            if (run == "__FilterToConsumerBinding")
            {
                string? consumerClass = null, consumerName = null, filterName = null;
                for (var j = i + 1; j < Math.Min(runs.Count, i + 1 + LookaheadRuns); j++)
                {
                    var cm = ConsumerInstance.Match(runs[j]);
                    if (cm.Success) { consumerClass = cm.Groups[1].Value; consumerName = cm.Groups[2].Value; }
                    var fm = FilterRef.Match(runs[j]);
                    if (fm.Success) filterName = fm.Groups[1].Value;
                }
                // An instance binding names both a consumer and a filter; the class definition does not.
                if (consumerName != null && filterName != null)
                {
                    var key = $"{consumerClass}|{consumerName}|{filterName}";
                    if (seenBindings.Add(key))
                        records.Add(new WmiPersistenceRecord
                        {
                            Kind = "Binding",
                            ConsumerClass = consumerClass,
                            ConsumerName = consumerName,
                            FilterName = filterName
                        });
                }
            }
            else if (run == "__EventFilter")
            {
                string? ns = null, name = null, query = null, lang = null;
                for (var j = i + 1; j < Math.Min(runs.Count, i + 1 + LookaheadRuns); j++)
                {
                    var r = runs[j];
                    if (ns == null && NamespaceLike.IsMatch(r)) { ns = r; continue; }
                    if (query == null && WqlQuery.IsMatch(r)) { query = r; if (j + 1 < runs.Count) lang = runs[j + 1]; break; }
                    if (ns != null && name == null) name = r;
                }
                // A filter instance carries an actual query; the class definition only declares the "Query" property.
                if (query != null)
                {
                    var key = $"{name}|{query}";
                    if (seenFilters.Add(key))
                        records.Add(new WmiPersistenceRecord
                        {
                            Kind = "Filter",
                            Name = name,
                            Namespace = ns,
                            Query = query,
                            QueryLanguage = lang == "WQL" ? lang : null
                        });
                }
            }
            else
            {
                var cm = ConsumerInstance.Match(run);
                if (cm.Success)
                {
                    var cls = cm.Groups[1].Value;
                    var name = cm.Groups[2].Value;
                    var key = $"{cls}|{name}";
                    if (seenConsumers.Add(key))
                        records.Add(new WmiPersistenceRecord { Kind = "Consumer", ConsumerClass = cls, ConsumerName = name });
                }
            }
        }

        return records;
    }

    /// <summary>Extracts runs of printable ASCII (length ≥ 3) from the repository bytes.</summary>
    internal static List<string> ExtractAsciiRuns(byte[] bytes)
    {
        var runs = new List<string>();
        var start = -1;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            var printable = b >= 0x20 && b < 0x7F;
            if (printable)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                if (i - start >= MinRunLength)
                    runs.Add(System.Text.Encoding.ASCII.GetString(bytes, start, i - start));
                start = -1;
            }
        }
        if (start >= 0 && bytes.Length - start >= MinRunLength)
            runs.Add(System.Text.Encoding.ASCII.GetString(bytes, start, bytes.Length - start));
        return runs;
    }
}
