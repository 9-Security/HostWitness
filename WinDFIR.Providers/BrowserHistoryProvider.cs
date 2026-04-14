using Microsoft.Data.Sqlite;
using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;

namespace WinDFIR.Providers;

/// <summary>
/// Browser history provider: reads Chromium-based browser SQLite databases.
/// Per specification: BrowserHistoryProvider outputs Chromium-based browser SQLite DB.
/// </summary>
public class BrowserHistoryProvider : IProvider
{
    public string Name => "BrowserHistoryProvider";

    private static bool _sqliteInitialized;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessBrowserHistory(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

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

    private async Task ProcessBrowserHistory(CancellationToken cancellationToken)
    {
        var historyPaths = GetBrowserHistoryPaths(cancellationToken);

        foreach (var historyPath in historyPaths)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (File.Exists(historyPath))
            {
                try
                {
                    if (Path.GetFileName(historyPath).Equals("places.sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        await ProcessFirefoxHistoryFile(historyPath, cancellationToken);
                    }
                    else
                    {
                        await ProcessBrowserHistoryFile(historyPath, cancellationToken);
                    }
                }
                catch (IOException ex)
                {
                    CollectionWarnings.Add($"BrowserHistory: {Path.GetFileName(historyPath)} — {ex.Message}");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    CollectionWarnings.Add($"BrowserHistory: {Path.GetFileName(historyPath)} — {ex.Message}");
                    continue;
                }
                catch
                {
                    continue;
                }
            }
        }
    }

    /// <summary>Known paths are enumerated first; optional recursive scan is limited by max depth and respects cancellation.</summary>
    private static IEnumerable<string> GetBrowserHistoryPaths(CancellationToken cancellationToken = default)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var localRoots = new List<string> { localAppData };
        var roamingRoots = new List<string> { roamingAppData };

        // Also scan all user profiles (admin run may point to a different profile)
        var usersRoot = Path.GetFullPath(Path.Combine(localAppData, "..", ".."));
        if (Directory.Exists(usersRoot))
        {
            try
            {
                foreach (var userDir in Directory.GetDirectories(usersRoot))
                {
                    try
                    {
                        var userLocal = Path.Combine(userDir, "AppData", "Local");
                        if (Directory.Exists(userLocal))
                            localRoots.Add(userLocal);
                        var userRoaming = Path.Combine(userDir, "AppData", "Roaming");
                        if (Directory.Exists(userRoaming))
                            roamingRoots.Add(userRoaming);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        CollectionWarnings.Add($"BrowserHistory: skipped profile under {userDir} — {ex.Message}");
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                CollectionWarnings.Add($"BrowserHistory: could not enumerate {usersRoot} — {ex.Message}");
            }
            catch (IOException ex)
            {
                CollectionWarnings.Add($"BrowserHistory: could not enumerate {usersRoot} — {ex.Message}");
            }
        }

        // Optional custom paths via environment variable
        var customPaths = Environment.GetEnvironmentVariable("WINDFIR_BROWSER_HISTORY_PATHS");
        if (!string.IsNullOrWhiteSpace(customPaths))
        {
            foreach (var raw in customPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = raw.Trim().Trim('"');
                if (!string.IsNullOrEmpty(trimmed))
                {
                    results.Add(trimmed);
                }
            }
        }

        const int maxScanDepth = 8;

        foreach (var root in localRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Chromium-based browsers with profiles (known paths first)
            AddChromiumProfiles(results, Path.Combine(root, @"Google\Chrome\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"Google\Chrome Beta\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"Google\Chrome SxS\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"Microsoft\Edge\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"Microsoft\Edge Beta\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"Microsoft\Edge Dev\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"Microsoft\Edge SxS\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"BraveSoftware\Brave-Browser\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"BraveSoftware\Brave-Browser-Beta\User Data"));
            AddChromiumProfiles(results, Path.Combine(root, @"BraveSoftware\Brave-Browser-Nightly\User Data"));

            // Opera paths (no profile folders)
            results.Add(Path.Combine(root, @"Opera Software\Opera Stable\History"));
            results.Add(Path.Combine(root, @"Opera Software\Opera GX Stable\History"));

            // Optional scan for Chromium History under LocalAppData (depth-limited)
            AddHistoryFilesByScan(results, root, "History", maxScanDepth, cancellationToken);
        }

        foreach (var root in roamingRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Firefox profiles (places.sqlite)
            var firefoxProfiles = Path.Combine(root, @"Mozilla\Firefox\Profiles");
            AddFirefoxProfiles(results, firefoxProfiles);

            // Optional scan for places.sqlite under Roaming (depth-limited)
            AddHistoryFilesByScan(results, root, "places.sqlite", maxScanDepth, cancellationToken);
        }

        return results;
    }

    private static void AddChromiumProfiles(HashSet<string> results, string userDataRoot)
    {
        if (!Directory.Exists(userDataRoot))
            return;

        var defaultHistory = Path.Combine(userDataRoot, "Default", "History");
        results.Add(defaultHistory);

        try
        {
            foreach (var profileDir in Directory.GetDirectories(userDataRoot, "Profile *"))
                results.Add(Path.Combine(profileDir, "History"));
        }
        catch (UnauthorizedAccessException ex)
        {
            CollectionWarnings.Add($"BrowserHistory: Chromium profiles under {userDataRoot} — {ex.Message}");
        }
        catch (IOException ex)
        {
            CollectionWarnings.Add($"BrowserHistory: Chromium profiles under {userDataRoot} — {ex.Message}");
        }
    }

    private static void AddFirefoxProfiles(HashSet<string> results, string profilesRoot)
    {
        if (!Directory.Exists(profilesRoot))
            return;

        try
        {
            foreach (var profileDir in Directory.GetDirectories(profilesRoot))
                results.Add(Path.Combine(profileDir, "places.sqlite"));
        }
        catch (UnauthorizedAccessException ex)
        {
            CollectionWarnings.Add($"BrowserHistory: Firefox profiles under {profilesRoot} — {ex.Message}");
        }
        catch (IOException ex)
        {
            CollectionWarnings.Add($"BrowserHistory: Firefox profiles under {profilesRoot} — {ex.Message}");
        }
    }

    private static void AddHistoryFilesByScan(
        HashSet<string> results,
        string root,
        string filename,
        int maxDepth = 8,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root) || maxDepth <= 0) return;

        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (currentDir, depth) = stack.Pop();
            if (depth >= maxDepth)
                continue;

            try
            {
                foreach (var file in Directory.GetFiles(currentDir, filename))
                {
                    results.Add(file);
                }

                foreach (var dir in Directory.GetDirectories(currentDir))
                {
                    stack.Push((dir, depth + 1));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we don't have permission to access
            }
            catch (PathTooLongException)
            {
                // Skip paths that are too long
            }
            catch
            {
                // Ignore other scanning errors
            }
        }
    }

    private static void CopyFileShared(string sourcePath, string destPath)
    {
        try
        {
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            sourceStream.CopyTo(destStream);
            destStream.Flush(true);
        }
        catch (IOException ex)
        {
            CollectionWarnings.Add($"BrowserHistory (copy): {Path.GetFileName(sourcePath)} — {ex.Message}");
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            CollectionWarnings.Add($"BrowserHistory (copy): {Path.GetFileName(sourcePath)} — {ex.Message}");
            throw;
        }
    }

    private static void CopySqliteWithWal(string sourcePath, string destPath)
    {
        CopyFileShared(sourcePath, destPath);

        var wal = sourcePath + "-wal";
        var shm = sourcePath + "-shm";
        var destWal = destPath + "-wal";
        var destShm = destPath + "-shm";

        if (File.Exists(wal))
        {
            CopyFileShared(wal, destWal);
        }
        if (File.Exists(shm))
        {
            CopyFileShared(shm, destShm);
        }
    }

    private static (string Browser, string UserProfile, string BrowserProfile) GetChromiumContext(string historyPath)
    {
        var browser = "Chromium";
        if (historyPath.Contains(@"\Google\Chrome\", StringComparison.OrdinalIgnoreCase)) browser = "Chrome (Chromium-based)";
        else if (historyPath.Contains(@"\Microsoft\Edge\", StringComparison.OrdinalIgnoreCase)) browser = "Edge (Chromium-based)";
        else if (historyPath.Contains(@"\BraveSoftware\Brave-Browser\", StringComparison.OrdinalIgnoreCase)) browser = "Brave (Chromium-based)";
        else if (historyPath.Contains(@"\Opera Software\", StringComparison.OrdinalIgnoreCase)) browser = "Opera (Chromium-based)";

        var userProfile = "Unknown";
        try
        {
            var parts = historyPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var usersIndex = Array.FindIndex(parts, p => p.Equals("Users", StringComparison.OrdinalIgnoreCase));
            if (usersIndex >= 0 && usersIndex + 1 < parts.Length)
            {
                userProfile = parts[usersIndex + 1];
            }
        }
        catch
        {
            // ignore
        }

        var browserProfile = "Default";
        if (historyPath.Contains(@"\Profile ", StringComparison.OrdinalIgnoreCase))
        {
            var segments = historyPath.Split(Path.DirectorySeparatorChar);
            var profileSegment = segments.LastOrDefault(s => s.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(profileSegment))
            {
                browserProfile = profileSegment;
            }
        }

        return (browser, userProfile, browserProfile);
    }

    private static (string UserProfile, string BrowserProfile) GetFirefoxContext(string historyPath)
    {
        var userProfile = "Unknown";
        try
        {
            var parts = historyPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var usersIndex = Array.FindIndex(parts, p => p.Equals("Users", StringComparison.OrdinalIgnoreCase));
            if (usersIndex >= 0 && usersIndex + 1 < parts.Length)
            {
                userProfile = parts[usersIndex + 1];
            }
        }
        catch
        {
            // ignore
        }

        var browserProfile = "Default";
        try
        {
            var profilesIndex = historyPath.IndexOf(@"\Mozilla\Firefox\Profiles\", StringComparison.OrdinalIgnoreCase);
            if (profilesIndex >= 0)
            {
                var profilePart = historyPath.Substring(profilesIndex + @"\Mozilla\Firefox\Profiles\".Length);
                var folder = profilePart.Split(Path.DirectorySeparatorChar).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    browserProfile = folder;
                }
            }
        }
        catch
        {
            // ignore
        }

        return (userProfile, browserProfile);
    }

    private async Task ProcessFirefoxHistoryFile(string historyPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!_sqliteInitialized)
            {
                SQLitePCL.Batteries_V2.Init();
                _sqliteInitialized = true;
            }

            var tempDbPath = Path.Combine(Path.GetTempPath(), $"places_{Guid.NewGuid()}.sqlite");
            try
            {
                CopySqliteWithWal(historyPath, tempDbPath);

                var connectionString = $"Data Source={tempDbPath};Mode=ReadOnly;";
                using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                var query = @"
                    SELECT
                        p.id,
                        p.url,
                        p.title,
                        p.visit_count,
                        v.visit_date,
                        v.visit_type
                    FROM moz_places p
                    JOIN moz_historyvisits v ON v.place_id = p.id
                    ORDER BY v.visit_date DESC
                    LIMIT 50000";

                using var command = new SqliteCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    var url = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var visitCount = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                    if (reader.IsDBNull(4))
                        continue;

                    var visitDate = reader.GetInt64(4);
                    if (visitDate <= 0)
                        continue;

                    // Firefox visit_date is microseconds since Unix epoch
                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(visitDate / 1000).UtcDateTime;

                    // Map visit_type
                    var visitTypeInt = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                    string visitType = visitTypeInt switch
                    {
                        1 => "Link",
                        2 => "Typed",
                        3 => "Bookmark",
                        4 => "Embed",
                        5 => "Redirect Perm",
                        6 => "Redirect Temp",
                        7 => "Download",
                        _ => visitTypeInt.ToString()
                    };

                    var (userProfile, browserProfile) = GetFirefoxContext(historyPath);

                    var evidence = new List<EvidenceRef>
                    {
                        new EvidenceRef(
                            "BrowserHistory",
                            $"{historyPath}:{reader.GetInt64(0)}",
                            null,
                            timestamp)
                    };

                    var activityEvent = new ActivityEvent
                    {
                        Category = "Browser",
                        Action = "Visit",
                        Timestamp = timestamp,
                        Evidence = evidence,
                        ObjectUrl = url,
                        Summary = $"Browser visit: {title}",
                        Fields = new Dictionary<string, object>
                        {
                            ["Url"] = url,
                            ["Title"] = title,
                            ["VisitCount"] = visitCount,
                            ["VisitType"] = visitType,
                            ["HistoryDatabase"] = historyPath,
                            ["RecordId"] = reader.GetInt64(0),
                            ["WebBrowser"] = "Firefox",
                            ["UserProfile"] = userProfile,
                            ["BrowserProfile"] = browserProfile
                        },
                        Confidence = "High"
                    };

                    EventProduced?.Invoke(this, activityEvent);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempDbPath))
                        File.Delete(tempDbPath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        catch
        {
            // Skip databases we can't process
        }
    }

    private async Task ProcessBrowserHistoryFile(string historyPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!_sqliteInitialized)
            {
                SQLitePCL.Batteries_V2.Init();
                _sqliteInitialized = true;
            }

            // Copy database to temp location to avoid locking issues
            var tempDbPath = Path.Combine(Path.GetTempPath(), $"history_{Guid.NewGuid()}.db");
            
            try
            {
                CopySqliteWithWal(historyPath, tempDbPath);

                var connectionString = $"Data Source={tempDbPath};Mode=ReadOnly;";
                
                using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                // Query visits + urls (more reliable than last_visit_time only)
                var query = @"
                    SELECT 
                        urls.id,
                        urls.url,
                        urls.title,
                        urls.visit_count,
                        urls.typed_count,
                        visits.visit_time,
                        visits.transition
                    FROM urls
                    JOIN visits ON visits.url = urls.id
                    ORDER BY visits.visit_time DESC
                    LIMIT 50000";

                using var command = new SqliteCommand(query, connection);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    if (reader.IsDBNull(1))
                        continue;

                    var url = reader.GetString(1);
                    var title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var visitCount = reader.GetInt64(3);
                    var typedCount = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);

                    if (reader.IsDBNull(5))
                        continue;
                    
                    var visitTime = reader.GetInt64(5);
                    if (visitTime <= 0)
                        continue;

                    // Chromium visit_time is microseconds since 1601-01-01 (1us = 10 ticks)
                    var timestamp = TimeNormalizer.FromFileTime((ulong)(visitTime * 10));
                    
                    // Additional validation: skip if timestamp is before 2000-01-01 (likely invalid)
                    if (timestamp < new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                        continue;

                    // Map transition (mask 0xFF for core type)
                    var transitionInt = reader.IsDBNull(6) ? 0 : reader.GetInt64(6);
                    var coreTransition = transitionInt & 0xFF;
                    string visitType = coreTransition switch
                    {
                        0 => "Link",
                        1 => "Typed",
                        2 => "Auto Bookmark",
                        3 => "Auto Subframe",
                        4 => "Manual Subframe",
                        5 => "Generated",
                        6 => "Start Page",
                        7 => "Form Submit",
                        8 => "Reload",
                        _ => coreTransition.ToString()
                    };

                    var (browserName, userProfile, browserProfile) = GetChromiumContext(historyPath);

                    var evidence = new List<EvidenceRef>
                    {
                        new EvidenceRef(
                            "BrowserHistory",
                            $"{historyPath}:{reader.GetInt64(0)}",
                            null,
                            timestamp)
                    };

                    var activityEvent = new ActivityEvent
                    {
                        Category = "Browser",
                        Action = "Visit",
                        Timestamp = timestamp,
                        Evidence = evidence,
                        ObjectUrl = url,
                        Summary = $"Browser visit: {title}",
                        Fields = new Dictionary<string, object>
                        {
                            ["Url"] = url,
                            ["Title"] = title,
                            ["VisitCount"] = visitCount,
                            ["TypedCount"] = typedCount,
                            ["VisitType"] = visitType,
                            ["HistoryDatabase"] = historyPath,
                            ["RecordId"] = reader.GetInt64(0),
                            ["WebBrowser"] = browserName,
                            ["UserProfile"] = userProfile,
                            ["BrowserProfile"] = browserProfile
                        },
                        Confidence = "High"
                    };

                    EventProduced?.Invoke(this, activityEvent);
                }
            }
            finally
            {
                // Clean up temp file
                try
                {
                    if (File.Exists(tempDbPath))
                        File.Delete(tempDbPath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        catch
        {
            // Skip databases we can't process
        }
    }
}
