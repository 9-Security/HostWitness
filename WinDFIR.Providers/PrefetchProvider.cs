using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// Prefetch provider: reads *.pf execution artifacts.
/// Per specification: PrefetchProvider outputs *.pf execution artifacts.
/// </summary>
public class PrefetchProvider : IProvider
{
    public string Name => "PrefetchProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessPrefetchFiles(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

        return Task.CompletedTask;
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
                // Expected when stopping
            }
        }
    }

    private async Task ProcessPrefetchFiles(CancellationToken cancellationToken)
    {
        var prefetchPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

        if (!Directory.Exists(prefetchPath))
            return;

        try
        {
            var prefetchFiles = Directory.GetFiles(prefetchPath, "*.pf");

            foreach (var filePath in prefetchFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    await ProcessPrefetchFile(filePath, cancellationToken);
                }
                catch (IOException ex)
                {
                    CollectionWarnings.Add($"Prefetch: {Path.GetFileName(filePath)} — {ex.Message}");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    CollectionWarnings.Add($"Prefetch: {Path.GetFileName(filePath)} — {ex.Message}");
                    continue;
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Prefetch folder access may require admin privileges
        }
    }

    private async Task ProcessPrefetchFile(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var record = PrefetchParser.Parse(filePath);
            if (record == null)
                return;

            if (cancellationToken.IsCancellationRequested)
                return;

            var runTime = record.RunTimesUtc.FirstOrDefault();
            var hash = await ComputeFileHash(filePath);
            var evidence = new List<EvidenceRef>
            {
                new EvidenceRef(
                    "Prefetch",
                    filePath,
                    hash,
                    runTime == default ? (DateTime?)null : runTime)
            };

            var fileKey = KeyGenerator.GenerateFileKey(
                null,
                null,
                filePath,
                hash);

            var activityEvent = new ActivityEvent
            {
                Category = "File",
                Action = "Execute",
                Timestamp = runTime == default ? record.ModifiedTimeUtc : runTime,
                Evidence = evidence,
                ObjectFile = fileKey,
                Summary = $"Prefetch execution: {record.ProcessExe}",
                Fields = new Dictionary<string, object>
                {
                    ["ExecutableName"] = record.ProcessExe,
                    ["ProcessPath"] = record.ProcessPath,
                    ["PrefetchFileName"] = record.PrefetchFileName,
                    ["PrefetchFilePath"] = record.PrefetchFilePath,
                    ["RunCount"] = record.RunCount,
                    ["LastRunTime"] = runTime == default ? string.Empty : runTime.ToString("O"),
                    ["RunTimes"] = string.Join(";", record.RunTimesUtc.Select(t => t.ToString("O"))),
                    ["FileSize"] = record.FileSize,
                    ["FileCreated"] = record.CreatedTimeUtc.ToString("O"),
                    ["FileModified"] = record.ModifiedTimeUtc.ToString("O")
                },
                Confidence = "High"
            };

            EventProduced?.Invoke(this, activityEvent);
        }
        catch (IOException ex)
        {
            CollectionWarnings.Add($"Prefetch: {Path.GetFileName(filePath)} — {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            CollectionWarnings.Add($"Prefetch: {Path.GetFileName(filePath)} — {ex.Message}");
        }
        catch
        {
            // Skip files we can't parse
        }
    }

    private async Task<string> ComputeFileHash(string filePath)
    {
        try
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
