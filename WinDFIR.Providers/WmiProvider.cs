using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// WMI provider: recovers WMI event-subscription persistence (filters, consumers, bindings) from one or more
/// <c>OBJECTS.DATA</c> CIM repositories. One-shot, opt-in (supply a path via <see cref="AddRepository"/> —
/// UI "Load WMI..." or agent <c>--wmi=</c>). Triage-level: surfaces the subscription picture, not a full CIM parse.
/// </summary>
public class WmiProvider : IProvider
{
    public string Name => "WmiProvider";

    private readonly List<string> _repositoryPaths;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public WmiProvider(IEnumerable<string>? repositoryPaths = null)
    {
        _repositoryPaths = repositoryPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
    }

    /// <summary>Registers an <c>OBJECTS.DATA</c> path to parse on Start. No-op for blank paths.</summary>
    public void AddRepository(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _repositoryPaths.Add(path);
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
        foreach (var path in _repositoryPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                CollectionWarnings.Add($"WMI: repository not found — {path}");
                continue;
            }

            DateTime anchor;
            try { anchor = File.GetLastWriteTimeUtc(path); }
            catch { anchor = DateTime.UtcNow; }

            try
            {
                foreach (var record in WmiPersistenceParser.Parse(path))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    EmitRecord(path, anchor, record);
                }
            }
            catch (Exception ex)
            {
                CollectionWarnings.Add($"WMI: failed to parse {Path.GetFileName(path)} — {ex.Message}");
            }
        }
    }

    private void EmitRecord(string path, DateTime anchor, WmiPersistenceRecord record)
    {
        var fields = new Dictionary<string, object>
        {
            ["Mode"] = "Offline",
            ["Parser"] = "WMI",
            ["WmiKind"] = record.Kind
        };
        if (!string.IsNullOrEmpty(record.Name)) fields["Name"] = record.Name!;
        if (!string.IsNullOrEmpty(record.ConsumerClass)) fields["ConsumerClass"] = record.ConsumerClass!;
        if (!string.IsNullOrEmpty(record.ConsumerName)) fields["ConsumerName"] = record.ConsumerName!;
        if (!string.IsNullOrEmpty(record.FilterName)) fields["FilterName"] = record.FilterName!;
        if (!string.IsNullOrEmpty(record.Namespace)) fields["Namespace"] = record.Namespace!;
        if (!string.IsNullOrEmpty(record.Query)) fields["Query"] = record.Query!;
        if (!string.IsNullOrEmpty(record.QueryLanguage)) fields["QueryLanguage"] = record.QueryLanguage!;

        var summary = record.Kind switch
        {
            "Binding" => $"WMI binding: consumer '{record.ConsumerName}' ({record.ConsumerClass}) ← filter '{record.FilterName}'",
            "Filter" => $"WMI filter '{record.Name}': {record.Query}",
            "Consumer" => $"WMI consumer '{record.ConsumerName}' ({record.ConsumerClass})",
            _ => "WMI persistence record"
        };

        var activityEvent = new ActivityEvent
        {
            Category = "Persistence",
            Action = "WmiSubscription",
            Timestamp = anchor,
            Evidence = new List<EvidenceRef> { new EvidenceRef("WmiRepository", path, null, null) },
            Summary = summary,
            Fields = fields,
            Confidence = "Medium"
        };

        EventProduced?.Invoke(this, activityEvent);
    }
}
