using System.Text.RegularExpressions;
using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// BITS provider: recovers Background Intelligent Transfer Service queue entries from one or more
/// <c>qmgr.db</c> databases and emits one event per record (download files and jobs). One-shot, opt-in
/// (supply a database path via <see cref="AddDatabase"/> — UI "Load BITS..." or agent <c>--bits=</c>).
/// </summary>
public class BitsProvider : IProvider
{
    public string Name => "BitsProvider";

    private static readonly Regex SidPattern = new(@"^S-1-\d+(-\d+)+$", RegexOptions.Compiled);

    private readonly List<string> _databasePaths;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public BitsProvider(IEnumerable<string>? databasePaths = null)
    {
        _databasePaths = databasePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
    }

    /// <summary>Registers a <c>qmgr.db</c> path to parse on Start. No-op for blank paths.</summary>
    public void AddDatabase(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _databasePaths.Add(path);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => Process(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    /// <summary>Starts parsing and awaits completion. For on-demand UI loads.</summary>
    public async Task RunToCompletionAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        if (_processingTask != null)
            await _processingTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource?.Cancel();
        if (_processingTask != null)
        {
            try { await _processingTask; }
            catch (OperationCanceledException) { }
        }
    }

    private void Process(CancellationToken cancellationToken)
    {
        foreach (var dbPath in _databasePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                CollectionWarnings.Add($"BITS: database not found — {dbPath}");
                continue;
            }

            DateTime anchor;
            try { anchor = File.GetLastWriteTimeUtc(dbPath); }
            catch { anchor = DateTime.UtcNow; }

            try
            {
                foreach (var record in BitsParser.Parse(dbPath))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    EmitRecord(dbPath, anchor, record);
                }
            }
            catch (Exception ex)
            {
                CollectionWarnings.Add($"BITS: failed to parse {Path.GetFileName(dbPath)} — {ex.Message}");
            }
        }
    }

    private void EmitRecord(string dbPath, DateTime anchor, BitsRecord record)
    {
        var sids = record.OtherStrings.Where(s => SidPattern.IsMatch(s)).ToList();
        var names = record.OtherStrings.Where(s => !SidPattern.IsMatch(s)).ToList();

        var fields = new Dictionary<string, object>
        {
            ["Mode"] = "Offline",
            ["Parser"] = "BITS",
            ["RecordKind"] = record.Kind,
            ["RecordId"] = record.Id.ToString()
        };
        if (record.Urls.Count > 0)
            fields["Urls"] = string.Join(" | ", record.Urls);
        if (record.LocalPaths.Count > 0)
            fields["LocalPaths"] = string.Join(" | ", record.LocalPaths);
        if (sids.Count > 0)
            fields["OwnerSid"] = string.Join(" | ", sids);
        if (names.Count > 0)
            fields["Strings"] = string.Join(" | ", names);

        var category = record.Kind == "File" ? "Network" : "System";
        var summary = BuildSummary(record, names);

        var activityEvent = new ActivityEvent
        {
            Category = category,
            Action = "Query",
            Timestamp = anchor,
            Evidence = new List<EvidenceRef> { new EvidenceRef("BitsDb", dbPath, null, null) },
            Summary = summary,
            Fields = fields,
            Confidence = "Medium"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    private static string BuildSummary(BitsRecord record, List<string> names)
    {
        if (record.Kind == "File")
        {
            var url = record.Urls.FirstOrDefault();
            var path = record.LocalPaths.FirstOrDefault();
            if (url != null && path != null)
                return $"BITS download: {url} → {path}";
            if (url != null)
                return $"BITS download: {url}";
            if (path != null)
                return $"BITS file: {path}";
            return $"BITS file record {record.Id}";
        }

        var name = names.FirstOrDefault();
        return name != null ? $"BITS job: {name}" : $"BITS job {record.Id}";
    }
}
