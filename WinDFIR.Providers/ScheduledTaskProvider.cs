using System.Security.Cryptography;
using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// Scheduled Task provider: parses on-disk Task Scheduler definitions under
/// <c>%WinDir%\System32\Tasks</c> (recursively). Each task becomes one Persistence event
/// carrying triggers, actions, principal, and registration metadata.
/// One-shot static provider (like RecentLnk/JumpList): scans on Start, then completes.
/// </summary>
public class ScheduledTaskProvider : IProvider
{
    public string Name => "ScheduledTaskProvider";

    private readonly string _tasksRoot;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    /// <param name="tasksRootOverride">Optional override for the Tasks directory (used by tests).</param>
    public ScheduledTaskProvider(string? tasksRootOverride = null)
    {
        _tasksRoot = tasksRootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessTasks(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
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

    private void ProcessTasks(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_tasksRoot))
            return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_tasksRoot, "*", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            CollectionWarnings.Add($"ScheduledTask: access denied enumerating {_tasksRoot} (Administrator may be required).");
            return;
        }
        catch (IOException ex)
        {
            CollectionWarnings.Add($"ScheduledTask: {ex.Message}");
            return;
        }

        foreach (var filePath in files)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                ProcessTaskFile(filePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                CollectionWarnings.Add($"ScheduledTask: {Path.GetFileName(filePath)} — {ex.Message}");
            }
            catch (IOException ex)
            {
                CollectionWarnings.Add($"ScheduledTask: {Path.GetFileName(filePath)} — {ex.Message}");
            }
            catch
            {
                // Skip files we cannot parse.
            }
        }
    }

    private void ProcessTaskFile(string filePath)
    {
        var record = ScheduledTaskParser.Parse(filePath, BuildTaskName(filePath));
        if (record is null)
            return;

        var hash = ComputeFileHash(filePath);
        var timestamp = ResolveTimestamp(record, filePath);

        var evidence = new List<EvidenceRef>
        {
            new EvidenceRef("ScheduledTask", filePath, hash, record.RegistrationDateUtc)
        };

        FileKey? objectFile = null;
        var primaryExec = record.PrimaryExec;
        if (primaryExec?.Command is { } command && Path.IsPathRooted(command))
            objectFile = KeyGenerator.GenerateFileKey(null, null, command, null);

        var activityEvent = new ActivityEvent
        {
            Category = "Persistence",
            Action = "ScheduledTask",
            Timestamp = timestamp,
            Evidence = evidence,
            ObjectFile = objectFile,
            Summary = BuildSummary(record),
            Fields = BuildFields(record),
            Confidence = "High"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    /// <summary>Canonical task name relative to the configured Tasks root (e.g. <c>\Microsoft\Windows\...</c>).</summary>
    private string BuildTaskName(string filePath)
    {
        try
        {
            var relative = Path.GetRelativePath(_tasksRoot, filePath);
            if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
                return ScheduledTaskParser.DeriveTaskNameFromPath(filePath);
            return "\\" + relative
                .Replace(Path.DirectorySeparatorChar, '\\')
                .Replace(Path.AltDirectorySeparatorChar, '\\');
        }
        catch
        {
            return ScheduledTaskParser.DeriveTaskNameFromPath(filePath);
        }
    }

    private static DateTime ResolveTimestamp(ScheduledTaskRecord record, string filePath)
    {
        if (record.RegistrationDateUtc is { } reg)
            return reg;

        var earliestTrigger = record.Triggers
            .Where(t => t.StartBoundaryUtc.HasValue)
            .Select(t => t.StartBoundaryUtc!.Value)
            .DefaultIfEmpty(default)
            .Min();
        if (earliestTrigger != default)
            return earliestTrigger;

        try
        {
            return File.GetLastWriteTimeUtc(filePath);
        }
        catch
        {
            return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        }
    }

    private static string BuildSummary(ScheduledTaskRecord record)
    {
        var exec = record.PrimaryExec;
        if (exec?.Command is { } command)
        {
            var args = string.IsNullOrWhiteSpace(exec.Arguments) ? "" : $" {exec.Arguments}";
            return $"Scheduled task: {record.TaskName} → {command}{args}".Trim();
        }

        var comHandler = record.Actions.FirstOrDefault(a => string.Equals(a.Type, "ComHandler", StringComparison.OrdinalIgnoreCase));
        if (comHandler?.ComClassId is { } classId)
            return $"Scheduled task: {record.TaskName} → COM handler {classId}";

        return $"Scheduled task: {record.TaskName}";
    }

    private static Dictionary<string, object> BuildFields(ScheduledTaskRecord record)
    {
        var fields = new Dictionary<string, object>
        {
            ["TaskName"] = record.TaskName,
            ["TriggerCount"] = record.Triggers.Count,
            ["ActionCount"] = record.Actions.Count
        };

        AddIfPresent(fields, "TaskFilePath", record.SourceFilePath);
        AddIfPresent(fields, "Author", record.Author);
        AddIfPresent(fields, "Description", record.Description);
        AddIfPresent(fields, "URI", record.Uri);
        AddIfPresent(fields, "RegistrationDate", record.RegistrationDateUtc?.ToString("O"));
        AddIfPresent(fields, "PrincipalUserId", record.PrincipalUserId);
        AddIfPresent(fields, "PrincipalGroupId", record.PrincipalGroupId);
        AddIfPresent(fields, "RunLevel", record.RunLevel);
        AddIfPresent(fields, "LogonType", record.LogonType);
        if (record.Enabled.HasValue)
            fields["Enabled"] = record.Enabled.Value;
        if (record.Hidden.HasValue)
            fields["Hidden"] = record.Hidden.Value;

        var exec = record.PrimaryExec;
        if (exec != null)
        {
            AddIfPresent(fields, "ExecCommand", exec.Command);
            AddIfPresent(fields, "ExecArguments", exec.Arguments);
            AddIfPresent(fields, "ExecWorkingDirectory", exec.WorkingDirectory);
        }

        if (record.Triggers.Count > 0)
            fields["Triggers"] = string.Join("; ", record.Triggers.Select(FormatTrigger));
        if (record.Actions.Count > 0)
            fields["Actions"] = string.Join("; ", record.Actions.Select(FormatAction));

        return fields;
    }

    private static string FormatTrigger(ScheduledTaskTrigger t)
    {
        var parts = t.Type;
        if (!string.IsNullOrWhiteSpace(t.StartBoundary))
            parts += $"@{t.StartBoundary}";
        if (t.Enabled == false)
            parts += " (disabled)";
        return parts;
    }

    private static string FormatAction(ScheduledTaskAction a)
    {
        if (string.Equals(a.Type, "Exec", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = a.Command ?? "";
            return string.IsNullOrWhiteSpace(a.Arguments) ? $"Exec:{cmd}" : $"Exec:{cmd} {a.Arguments}";
        }
        if (string.Equals(a.Type, "ComHandler", StringComparison.OrdinalIgnoreCase))
            return $"ComHandler:{a.ComClassId}";
        return a.Type;
    }

    private static void AddIfPresent(Dictionary<string, object> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields[key] = value;
    }

    private static string ComputeFileHash(string filePath)
    {
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }
}
