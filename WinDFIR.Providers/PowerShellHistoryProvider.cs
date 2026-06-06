using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// PowerShell history provider: parses each user's PSReadLine
/// <c>ConsoleHost_history.txt</c> and emits one event per command line.
/// One-shot static provider (like RecentLnk/ScheduledTask): scans on Start, then completes.
/// </summary>
public class PowerShellHistoryProvider : IProvider
{
    public string Name => "PowerShellHistoryProvider";

    // Relative to a user profile directory.
    private static readonly string HistoryRelativePath = Path.Combine(
        "AppData", "Roaming", "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt");

    private readonly string _usersRoot;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    /// <param name="usersRootOverride">Optional override for the user-profiles root (e.g. C:\Users); used by tests.</param>
    public PowerShellHistoryProvider(string? usersRootOverride = null)
    {
        _usersRoot = usersRootOverride ?? ResolveUsersRoot();
    }

    private static string ResolveUsersRoot()
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var parent = Directory.GetParent(profile)?.FullName;
            if (!string.IsNullOrEmpty(parent))
                return parent;
        }
        catch
        {
            // fall through to default
        }

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        return systemDrive + Path.DirectorySeparatorChar + "Users";
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessHistories(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
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
                // Expected when stopping.
            }
        }
    }

    private void ProcessHistories(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_usersRoot))
            return;

        IEnumerable<string> userDirs;
        try
        {
            userDirs = Directory.EnumerateDirectories(_usersRoot);
        }
        catch (UnauthorizedAccessException)
        {
            CollectionWarnings.Add($"PowerShellHistory: access denied enumerating {_usersRoot}.");
            return;
        }
        catch (IOException ex)
        {
            CollectionWarnings.Add($"PowerShellHistory: {ex.Message}");
            return;
        }

        foreach (var userDir in userDirs)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var historyPath = Path.Combine(userDir, HistoryRelativePath);
            if (!File.Exists(historyPath))
                continue;

            try
            {
                ProcessHistoryFile(historyPath, Path.GetFileName(userDir), cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                CollectionWarnings.Add($"PowerShellHistory: {Path.GetFileName(userDir)} — {ex.Message}");
            }
            catch (IOException ex)
            {
                CollectionWarnings.Add($"PowerShellHistory: {Path.GetFileName(userDir)} — {ex.Message}");
            }
            catch
            {
                // Skip files we cannot read.
            }
        }
    }

    private void ProcessHistoryFile(string historyPath, string userName, CancellationToken cancellationToken)
    {
        var content = File.ReadAllText(historyPath);
        var entries = PowerShellHistoryParser.ParseHistory(content);
        if (entries.Count == 0)
            return;

        // No per-line timestamps exist in the format; the file's last-write time is the only anchor.
        DateTime timestamp;
        try
        {
            timestamp = File.GetLastWriteTimeUtc(historyPath);
        }
        catch
        {
            timestamp = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        }

        foreach (var entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var fields = new Dictionary<string, object>
            {
                ["User"] = userName,
                ["Command"] = entry.Command,
                ["LineNumber"] = entry.LineNumber,
                ["HistoryFilePath"] = historyPath
            };
            if (entry.SuspiciousKeywords.Count > 0)
                fields["SuspiciousKeywords"] = string.Join(", ", entry.SuspiciousKeywords);

            var activityEvent = new ActivityEvent
            {
                Category = "PowerShell",
                Action = "ConsoleHostHistory",
                Timestamp = timestamp,
                Evidence = new List<EvidenceRef> { new EvidenceRef("PowerShellHistory", historyPath, null, null) },
                Summary = $"PowerShell history ({userName}): {entry.Command}",
                Fields = fields,
                // History records accepted commands, not proof of successful execution.
                Confidence = "Medium"
            };

            EventProduced?.Invoke(this, activityEvent);
        }
    }
}
