using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// Startup folder provider: enumerates the All-Users and per-user Startup folders and emits one
/// Persistence event per entry. For <c>.lnk</c> shortcuts the target executable is resolved so it can
/// be pivoted on. One-shot static provider (scans on Start, then completes).
/// </summary>
public class StartupFolderProvider : IProvider
{
    public string Name => "StartupFolderProvider";

    private const string PerUserStartupRelative =
        @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";

    private readonly List<string> _startupFolders;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    /// <param name="startupFoldersOverride">Optional explicit folder list (used by tests); otherwise resolved from the system.</param>
    public StartupFolderProvider(IEnumerable<string>? startupFoldersOverride = null)
    {
        _startupFolders = (startupFoldersOverride?.ToList()) ?? ResolveDefaultFolders();
    }

    private static List<string> ResolveDefaultFolders()
    {
        var folders = new List<string>();

        try
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
            if (!string.IsNullOrEmpty(common))
                folders.Add(common);
        }
        catch { /* ignore */ }

        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var usersRoot = Directory.GetParent(profile)?.FullName;
            if (!string.IsNullOrEmpty(usersRoot) && Directory.Exists(usersRoot))
            {
                foreach (var userDir in Directory.EnumerateDirectories(usersRoot))
                    folders.Add(Path.Combine(userDir, PerUserStartupRelative));
            }
        }
        catch { /* ignore */ }

        return folders;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessFolders(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
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

    private void ProcessFolders(CancellationToken cancellationToken)
    {
        foreach (var folder in _startupFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                continue;

            var user = DeriveUser(folder);

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException ex)
            {
                CollectionWarnings.Add($"StartupFolder: access denied {folder} — {ex.Message}");
                continue;
            }
            catch (IOException ex)
            {
                CollectionWarnings.Add($"StartupFolder: {ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                // desktop.ini is folder metadata, not a startup item.
                if (Path.GetFileName(entry).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    ProcessEntry(entry, folder, user);
                }
                catch (UnauthorizedAccessException ex)
                {
                    CollectionWarnings.Add($"StartupFolder: {Path.GetFileName(entry)} — {ex.Message}");
                }
                catch (IOException ex)
                {
                    CollectionWarnings.Add($"StartupFolder: {Path.GetFileName(entry)} — {ex.Message}");
                }
                catch
                {
                    // Skip entries we cannot process.
                }
            }
        }
    }

    private void ProcessEntry(string entryPath, string startupFolder, string user)
    {
        DateTime timestamp;
        try
        {
            timestamp = File.GetLastWriteTimeUtc(entryPath);
        }
        catch
        {
            timestamp = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        }

        var entryName = Path.GetFileName(entryPath);
        var fields = new Dictionary<string, object>
        {
            ["User"] = user,
            ["EntryName"] = entryName,
            ["EntryPath"] = entryPath,
            ["StartupFolder"] = startupFolder
        };

        FileKey? objectFile = null;
        var targetForObject = entryPath;

        if (entryName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var lnk = TryParseLnk(entryPath);
            if (lnk != null && !string.IsNullOrWhiteSpace(lnk.TargetPath))
            {
                fields["TargetPath"] = lnk.TargetPath;
                if (!string.IsNullOrWhiteSpace(lnk.Arguments))
                    fields["Arguments"] = lnk.Arguments;
                if (!string.IsNullOrWhiteSpace(lnk.WorkingDirectory))
                    fields["WorkingDirectory"] = lnk.WorkingDirectory;
                targetForObject = lnk.TargetPath;
            }
        }

        if (Path.IsPathRooted(targetForObject))
            objectFile = KeyGenerator.GenerateFileKey(null, null, targetForObject, null);

        var summary = fields.TryGetValue("TargetPath", out var tp)
            ? $"Startup ({user}): {entryName} → {tp}"
            : $"Startup ({user}): {entryName}";

        var activityEvent = new ActivityEvent
        {
            Category = "Persistence",
            Action = "StartupFolder",
            Timestamp = timestamp,
            Evidence = new List<EvidenceRef> { new EvidenceRef("StartupFolder", entryPath, null, null) },
            ObjectFile = objectFile,
            Summary = summary,
            Fields = fields,
            Confidence = "High"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    private static LnkParseResult? TryParseLnk(string path)
    {
        try
        {
            return LnkParser.Parse(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Derives the owning user from a startup folder path; "AllUsers" for the common/ProgramData folder.</summary>
    internal static string DeriveUser(string folderPath)
    {
        try
        {
            var parts = folderPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var usersIndex = Array.FindLastIndex(parts, p => p.Equals("Users", StringComparison.OrdinalIgnoreCase));
            if (usersIndex >= 0 && usersIndex < parts.Length - 1)
                return parts[usersIndex + 1];
        }
        catch { /* ignore */ }

        return "AllUsers";
    }
}
