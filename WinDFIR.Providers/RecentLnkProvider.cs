using System.IO;
using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.Providers;

/// <summary>
/// Recent LNK provider: reads Recent-folder LNK files only.
/// This provider is intentionally scoped to "%AppData%\Microsoft\Windows\Recent"
/// so emitted events keep a strict "recent access" forensic meaning.
/// </summary>
public class RecentLnkProvider : IProvider
{
    public string Name => "RecentLnkProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessRecentLnkFiles(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

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

    private async Task ProcessRecentLnkFiles(CancellationToken cancellationToken)
    {
        var recentPath = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrWhiteSpace(recentPath) || !Directory.Exists(recentPath))
            return;

        foreach (var filePath in EnumerateLnkFilesSafely(recentPath))
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                await ProcessLnkFile(filePath, cancellationToken);
            }
            catch (IOException ex)
            {
                CollectionWarnings.Add($"RecentLnk: {Path.GetFileName(filePath)} - {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                CollectionWarnings.Add($"RecentLnk: {Path.GetFileName(filePath)} - {ex.Message}");
            }
            catch
            {
                // Skip files we can't parse
            }
        }
    }

    /// <summary>
    /// Enumerates .lnk files recursively while tolerating per-directory access failures.
    /// </summary>
    private IEnumerable<string> EnumerateLnkFilesSafely(string rootPath)
    {
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                CollectionWarnings.Add($"RecentLnk: Access denied while listing {dir}");
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                CollectionWarnings.Add($"RecentLnk: Access denied while listing subdirs in {dir}");
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var subDir in subDirs)
                stack.Push(subDir);
        }
    }

    private async Task ProcessLnkFile(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var parsed = LnkParser.Parse(bytes);

            var targetPath = parsed.TargetPath;
            if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.NetworkPath))
            {
                targetPath = parsed.NetworkPath;
            }
            if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.RelativePath))
            {
                targetPath = parsed.RelativePath;
            }
            var usedRawFallback = false;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                // Minimal raw fallback: search likely path strings directly from LNK bytes.
                targetPath = TryExtractLikelyPathFromLnkBytes(bytes);
                usedRawFallback = !string.IsNullOrWhiteSpace(targetPath);
            }

            var creationTime = fileInfo.CreationTimeUtc;
            var accessTime = parsed.LinkAccessTimeUtc ?? fileInfo.LastAccessTimeUtc;
            var writeTime = parsed.LinkWriteTimeUtc ?? fileInfo.LastWriteTimeUtc;

            if (string.IsNullOrEmpty(targetPath))
                return;

            var evidence = new List<EvidenceRef>
            {
                new EvidenceRef(
                    "RecentLnk",
                    filePath,
                    await ComputeFileHash(filePath),
                    accessTime)
            };

            var fileKey = KeyGenerator.GenerateFileKey(
                null,
                null,
                targetPath,
                null);

            var activityEvent = new ActivityEvent
            {
                Category = "File",
                Action = "Open",
                Timestamp = accessTime,
                Evidence = evidence,
                ObjectFile = fileKey,
                Summary = $"Recent file access: {Path.GetFileName(targetPath)}",
                Fields = new Dictionary<string, object>
                {
                    ["LnkFilePath"] = filePath,
                    ["TargetPath"] = targetPath,
                    ["TargetFileName"] = Path.GetFileName(targetPath),
                    ["EvidenceSource"] = "RecentFolderLnk",
                    ["RawFallbackUsed"] = usedRawFallback,
                    ["PathConfidence"] = usedRawFallback ? "Low" : "Medium",
                    ["CreationTime"] = creationTime.ToString("O"),
                    ["AccessTime"] = accessTime.ToString("O"),
                    ["WriteTime"] = writeTime.ToString("O"),
                    ["FileSize"] = fileInfo.Length,
                    ["LinkLocalBasePath"] = parsed.LocalBasePath,
                    ["LinkCommonPathSuffix"] = parsed.CommonPathSuffix,
                    ["LinkNetworkPath"] = parsed.NetworkPath,
                    ["LinkRelativePath"] = parsed.RelativePath,
                    ["LinkNameString"] = parsed.NameString,
                    ["LinkWorkingDirectory"] = parsed.WorkingDirectory,
                    ["LinkArguments"] = parsed.Arguments,
                    ["LinkCreationTimeUtc"] = parsed.LinkCreationTimeUtc?.ToString("O") ?? string.Empty,
                    ["LinkAccessTimeUtc"] = parsed.LinkAccessTimeUtc?.ToString("O") ?? string.Empty,
                    ["LinkWriteTimeUtc"] = parsed.LinkWriteTimeUtc?.ToString("O") ?? string.Empty
                },
                Confidence = usedRawFallback ? "Low" : "Medium"
            };

            EventProduced?.Invoke(this, activityEvent);
        }
        catch (IOException ex)
        {
            CollectionWarnings.Add($"RecentLnk: {Path.GetFileName(filePath)} — {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            CollectionWarnings.Add($"RecentLnk: {Path.GetFileName(filePath)} — {ex.Message}");
        }
        catch
        {
            // Skip files we can't parse
        }
    }

    private static string TryExtractLikelyPathFromLnkBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        // UTF-16LE scan is preferred for .lnk string-data blocks.
        var unicode = System.Text.Encoding.Unicode.GetString(bytes);
        var path = ExtractLikelyPath(unicode);
        if (!string.IsNullOrEmpty(path))
            return path;

        // Fallback to ASCII scan for malformed or partially parsed records.
        var ascii = System.Text.Encoding.ASCII.GetString(bytes);
        return ExtractLikelyPath(ascii);
    }

    private static string ExtractLikelyPath(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Drive-letter local path: C:\...
        var drive = System.Text.RegularExpressions.Regex.Match(
            text,
            @"[A-Za-z]:\\[^\u0000\r\n\t\*<>|\""]+",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (drive.Success)
            return drive.Value.Trim();

        // UNC path: \\server\share\...
        var unc = System.Text.RegularExpressions.Regex.Match(
            text,
            @"\\\\[^\u0000\r\n\t\\/:*?""<>|]+\\[^\u0000\r\n\t:*?""<>|]+(?:\\[^\u0000\r\n\t:*?""<>|]+)*",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return unc.Success ? unc.Value.Trim() : string.Empty;
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
