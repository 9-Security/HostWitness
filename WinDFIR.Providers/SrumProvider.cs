using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// SRUM provider: parses one or more <c>SRUDB.dat</c> System Resource Usage Monitor databases and emits one
/// event per provider-table row (per-app network data usage, connectivity, application resource/energy usage).
/// One-shot static provider. Opt-in (not in the default live set) because SRUM is high-volume historical data;
/// supply a database path via <see cref="AddDatabase"/> (UI "Load SRUM..." or agent <c>--srum=</c>).
/// </summary>
public class SrumProvider : IProvider
{
    public string Name => "SrumProvider";

    /// <summary>Per provider-table row cap (matches the in-memory index default). Tables larger than this are truncated with a warning.</summary>
    public const int DefaultPerTableCap = 100_000;

    private static readonly HashSet<string> NetworkProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Network Data Usage", "Network Connectivity Usage"
    };

    private readonly List<string> _databasePaths;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public int PerTableCap { get; set; } = DefaultPerTableCap;

    public SrumProvider(IEnumerable<string>? databasePaths = null)
    {
        _databasePaths = databasePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
    }

    /// <summary>Registers a <c>SRUDB.dat</c> path to parse on Start. No-op for blank paths.</summary>
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

    /// <summary>Starts parsing and awaits completion (SRUM parsing has a definite end). For on-demand UI loads.</summary>
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
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping.
            }
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
                CollectionWarnings.Add($"SRUM: database not found — {dbPath}");
                continue;
            }

            try
            {
                var records = SrumParser.Parse(dbPath, PerTableCap, out var truncated);
                foreach (var record in records)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    EmitRecord(dbPath, record);
                }

                foreach (var provider in truncated.Distinct(StringComparer.OrdinalIgnoreCase))
                    CollectionWarnings.Add($"SRUM: '{provider}' table truncated to {PerTableCap:n0} rows (display cap); older rows not loaded.");
            }
            catch (Exception ex)
            {
                CollectionWarnings.Add($"SRUM: failed to parse {Path.GetFileName(dbPath)} — {ex.Message}");
            }
        }
    }

    private void EmitRecord(string dbPath, SrumRecord record)
    {
        var category = NetworkProviders.Contains(record.ProviderName) ? "Network" : "System";

        var fields = new Dictionary<string, object>
        {
            ["Mode"] = "Offline",
            ["Parser"] = "SRUM",
            ["Provider"] = record.ProviderName,
            ["ProviderGuid"] = record.ProviderGuid
        };
        if (!string.IsNullOrEmpty(record.App))
            fields["App"] = record.App!;
        if (!string.IsNullOrEmpty(record.UserSid))
            fields["UserSid"] = record.UserSid!;
        foreach (var kv in record.Fields)
        {
            if (kv.Value != null)
                fields[kv.Key] = kv.Value;
        }

        var summary = BuildSummary(record);

        var activityEvent = new ActivityEvent
        {
            Category = category,
            Action = "Query",
            Timestamp = record.TimestampUtc,
            Evidence = new List<EvidenceRef> { new EvidenceRef("SrumDb", dbPath, null, null) },
            Summary = summary,
            Fields = fields,
            Confidence = "Medium"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    private static string BuildSummary(SrumRecord record)
    {
        var app = string.IsNullOrEmpty(record.App) ? "(unknown app)" : record.App;
        if (record.ProviderName == "Network Data Usage"
            && record.Fields.TryGetValue("BytesSent", out var sent)
            && record.Fields.TryGetValue("BytesRecvd", out var recvd))
        {
            return $"SRUM Network Data Usage: {app} sent={sent} recvd={recvd}";
        }

        return $"SRUM {record.ProviderName}: {app}";
    }
}
