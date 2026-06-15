using System.IO;
using WinDFIR.Core;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using WinDFIR.Providers.Parsers;
using OpenMcdf;

namespace WinDFIR.Providers;

/// <summary>
/// Jump List provider: reads Automatic & Custom Jump Lists.
/// Per specification: JumpListProvider outputs Automatic & Custom Jump Lists.
/// </summary>
public class JumpListProvider : IProvider
{
    public string Name => "JumpListProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessJumpLists(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

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

    private async Task ProcessJumpLists(CancellationToken cancellationToken)
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var jumpListPath = Path.Combine(appDataPath, @"Microsoft\Windows\Recent\AutomaticDestinations");
        var customJumpListPath = Path.Combine(appDataPath, @"Microsoft\Windows\Recent\CustomDestinations");

        // Process Automatic Destinations
        if (Directory.Exists(jumpListPath))
        {
            await ProcessJumpListDirectory(jumpListPath, "Automatic", cancellationToken);
        }

        // Process Custom Destinations
        if (Directory.Exists(customJumpListPath))
        {
            await ProcessJumpListDirectory(customJumpListPath, "Custom", cancellationToken);
        }
    }

    private async Task ProcessJumpListDirectory(string directoryPath, string jumpListType, CancellationToken cancellationToken)
    {
        try
        {
            var files = Directory.GetFiles(directoryPath);

            foreach (var filePath in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    await ProcessJumpListFile(filePath, jumpListType, cancellationToken);
                }
                catch
                {
                    // Skip files we can't process
                    continue;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Jump list access may require permissions
        }
    }

    private async Task ProcessJumpListFile(string filePath, string jumpListType, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileName = Path.GetFileName(filePath);

            // Jump list files are LNK files or structured storage
            var accessTime = fileInfo.LastAccessTimeUtc;
            var modificationTime = fileInfo.LastWriteTimeUtc;

            var evidence = new List<EvidenceRef>
            {
                new EvidenceRef(
                    "JumpList",
                    filePath,
                    await ComputeFileHash(filePath),
                    accessTime)
            };

            // Try to extract application name from file name
            // Automatic destinations: [AppID].automaticDestinations-ms
            // Custom destinations: [AppID].customDestinations-ms
            var appId = fileName;
            var dashIndex = fileName.IndexOf('-');
            if (dashIndex > 0)
            {
                appId = fileName.Substring(0, dashIndex);
            }

            var emitted = false;
            if (jumpListType.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
            {
                emitted = await TryEmitAutomaticJumpListEntries(filePath, appId, jumpListType, evidence, accessTime, modificationTime, cancellationToken);
            }
            else if (jumpListType.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                emitted = await TryEmitCustomJumpListEntries(filePath, appId, jumpListType, evidence, accessTime, modificationTime, cancellationToken);
            }

            if (!emitted)
            {
                var activityEvent = new ActivityEvent
                {
                    Category = "File",
                    Action = "Open",
                    Timestamp = accessTime,
                    Evidence = evidence,
                    Summary = $"Jump List access: {appId} ({jumpListType})",
                    Fields = new Dictionary<string, object>
                    {
                        ["JumpListType"] = jumpListType,
                        ["AppId"] = appId,
                        ["FilePath"] = filePath,
                        ["FileName"] = fileName,
                        ["AccessTime"] = accessTime.ToString("O"),
                        ["ModificationTime"] = modificationTime.ToString("O"),
                        ["FileSize"] = fileInfo.Length,
                        ["IsContainer"] = true
                    },
                    Confidence = "Medium"
                };

                EventProduced?.Invoke(this, activityEvent);
            }
        }
        catch (IOException ex)
        {
            CollectionWarnings.Add($"JumpList: {Path.GetFileName(filePath)} — {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            CollectionWarnings.Add($"JumpList: {Path.GetFileName(filePath)} — {ex.Message}");
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

    private Task<bool> TryEmitAutomaticJumpListEntries(
        string filePath,
        string appId,
        string jumpListType,
        List<EvidenceRef> evidence,
        DateTime accessTime,
        DateTime modificationTime,
        CancellationToken cancellationToken)
    {
        try
        {
            using var root = RootStorage.OpenRead(filePath);
            var entries = root.EnumerateEntries();
            var any = false;
            var destListMap = BuildDestListMap(root);

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var entryName = entry.Name;
                if (entryName.Equals("DestList", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!root.TryOpenStream(entryName, out var cfbStream))
                    continue;

                using var stream = cfbStream;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var data = ms.ToArray();
                var parsed = LnkParser.Parse(data);
                var destInfo = destListMap.TryGetValue(entryName, out var info) ? info : null;

                var targetPath = parsed.TargetPath;
                if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.NetworkPath))
                {
                    targetPath = parsed.NetworkPath;
                }
                if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.RelativePath))
                {
                    targetPath = parsed.RelativePath;
                }
                if (string.IsNullOrWhiteSpace(targetPath) && destInfo != null && !string.IsNullOrWhiteSpace(destInfo.PathHint))
                {
                    targetPath = destInfo.PathHint;
                }

                if (string.IsNullOrWhiteSpace(targetPath))
                    continue;

                var timestamp = destInfo?.LastAccessTimeUtc ?? parsed.LinkAccessTimeUtc ?? accessTime;
                var evt = new ActivityEvent
                {
                    Category = "File",
                    Action = "Open",
                    Timestamp = timestamp,
                    Evidence = evidence,
                    Summary = $"Jump List item: {Path.GetFileName(targetPath)} ({appId})",
                    Fields = new Dictionary<string, object>
                    {
                        ["JumpListType"] = jumpListType,
                        ["AppId"] = appId,
                        ["FilePath"] = filePath,
                        ["StreamName"] = entryName,
                        ["TargetPath"] = targetPath,
                        ["TargetFileName"] = Path.GetFileName(targetPath),
                        ["AccessTime"] = accessTime.ToString("O"),
                        ["ModificationTime"] = modificationTime.ToString("O"),
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
                    Confidence = "Medium"
                };

                if (destInfo != null)
                {
                    evt.Fields["JumpListMruOrder"] = destInfo.MruOrder;
                    evt.Fields["JumpListAccessCount"] = destInfo.AccessCount;
                    evt.Fields["JumpListPinStatus"] = destInfo.PinStatus;
                    if (destInfo.LastAccessTimeUtc.HasValue)
                        evt.Fields["JumpListLastAccessUtc"] = destInfo.LastAccessTimeUtc.Value.ToString("O");
                    if (!string.IsNullOrWhiteSpace(destInfo.PathHint))
                        evt.Fields["DestListPathHint"] = destInfo.PathHint;
                }

                any = true;
                EventProduced?.Invoke(this, evt);
            }

            return Task.FromResult(any);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private Task<bool> TryEmitCustomJumpListEntries(
        string filePath,
        string appId,
        string jumpListType,
        List<EvidenceRef> evidence,
        DateTime accessTime,
        DateTime modificationTime,
        CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                using var root = RootStorage.OpenRead(filePath);
                var entries = root.EnumerateEntries();
                var destListMap = BuildDestListMap(root);
                var anyStructured = false;
                var structuredOrder = 0;

                foreach (var entry in entries)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var entryName = entry.Name;
                    if (entryName.Equals("DestList", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!root.TryOpenStream(entryName, out var cfbStream))
                        continue;

                    using var stream = cfbStream;
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    var data = ms.ToArray();
                    var parsed = LnkParser.Parse(data);
                    var destInfo = destListMap.TryGetValue(entryName, out var info) ? info : null;

                    var targetPath = parsed.TargetPath;
                    if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.NetworkPath))
                    {
                        targetPath = parsed.NetworkPath;
                    }
                    if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.RelativePath))
                    {
                        targetPath = parsed.RelativePath;
                    }
                    if (string.IsNullOrWhiteSpace(targetPath) && destInfo != null && !string.IsNullOrWhiteSpace(destInfo.PathHint))
                    {
                        targetPath = destInfo.PathHint;
                    }

                    if (string.IsNullOrWhiteSpace(targetPath))
                        continue;

                    var timestamp = destInfo?.LastAccessTimeUtc ?? parsed.LinkAccessTimeUtc ?? accessTime;
                    var evt = new ActivityEvent
                    {
                        Category = "File",
                        Action = "Open",
                        Timestamp = timestamp,
                        Evidence = evidence,
                        Summary = $"Jump List item: {Path.GetFileName(targetPath)} ({appId})",
                        Fields = new Dictionary<string, object>
                        {
                            ["JumpListType"] = jumpListType,
                            ["AppId"] = appId,
                            ["FilePath"] = filePath,
                            ["StreamName"] = entryName,
                            ["TargetPath"] = targetPath,
                            ["TargetFileName"] = Path.GetFileName(targetPath),
                            ["AccessTime"] = accessTime.ToString("O"),
                            ["ModificationTime"] = modificationTime.ToString("O"),
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
                        Confidence = "Medium"
                    };

                    if (destInfo != null)
                    {
                        evt.Fields["JumpListMruOrder"] = destInfo.MruOrder;
                        evt.Fields["JumpListAccessCount"] = destInfo.AccessCount;
                        evt.Fields["JumpListPinStatus"] = destInfo.PinStatus;
                        if (destInfo.LastAccessTimeUtc.HasValue)
                            evt.Fields["JumpListLastAccessUtc"] = destInfo.LastAccessTimeUtc.Value.ToString("O");
                        if (!string.IsNullOrWhiteSpace(destInfo.PathHint))
                            evt.Fields["DestListPathHint"] = destInfo.PathHint;
                    }
                    else
                    {
                        evt.Fields["JumpListMruOrder"] = structuredOrder;
                    }

                    anyStructured = true;
                    EventProduced?.Invoke(this, evt);
                    structuredOrder++;
                }

                if (anyStructured)
                    return Task.FromResult(true);
            }
            catch (IOException ex)
            {
                CollectionWarnings.Add($"JumpList: {Path.GetFileName(filePath)} — {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                CollectionWarnings.Add($"JumpList: {Path.GetFileName(filePath)} — {ex.Message}");
            }
            catch
            {
                // Fallback to raw parsing for non-structured custom destinations
            }

            try
            {
            var bytes = File.ReadAllBytes(filePath);
            var any = false;
            var order = 0;

            foreach (var entryBytes in EnumerateCustomJumpListEntries(bytes))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var parsed = LnkParser.Parse(entryBytes);
                var targetPath = parsed.TargetPath;
                if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.NetworkPath))
                {
                    targetPath = parsed.NetworkPath;
                }
                if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.RelativePath))
                {
                    targetPath = parsed.RelativePath;
                }
                if (string.IsNullOrWhiteSpace(targetPath) && !string.IsNullOrWhiteSpace(parsed.NameString))
                {
                    targetPath = parsed.NameString;
                }

                if (string.IsNullOrWhiteSpace(targetPath))
                    continue;

                var timestamp = parsed.LinkAccessTimeUtc ?? accessTime;
                var evt = new ActivityEvent
                {
                    Category = "File",
                    Action = "Open",
                    Timestamp = timestamp,
                    Evidence = evidence,
                    Summary = $"Jump List item: {Path.GetFileName(targetPath)} ({appId})",
                    Fields = new Dictionary<string, object>
                    {
                        ["JumpListType"] = jumpListType,
                        ["AppId"] = appId,
                        ["FilePath"] = filePath,
                        ["StreamName"] = $"CustomEntry_{order}",
                        ["TargetPath"] = targetPath,
                        ["TargetFileName"] = Path.GetFileName(targetPath),
                        ["AccessTime"] = accessTime.ToString("O"),
                        ["ModificationTime"] = modificationTime.ToString("O"),
                        ["LinkLocalBasePath"] = parsed.LocalBasePath,
                        ["LinkCommonPathSuffix"] = parsed.CommonPathSuffix,
                        ["LinkNetworkPath"] = parsed.NetworkPath,
                        ["LinkRelativePath"] = parsed.RelativePath,
                        ["LinkNameString"] = parsed.NameString,
                        ["LinkWorkingDirectory"] = parsed.WorkingDirectory,
                        ["LinkArguments"] = parsed.Arguments,
                        ["LinkCreationTimeUtc"] = parsed.LinkCreationTimeUtc?.ToString("O") ?? string.Empty,
                        ["LinkAccessTimeUtc"] = parsed.LinkAccessTimeUtc?.ToString("O") ?? string.Empty,
                        ["LinkWriteTimeUtc"] = parsed.LinkWriteTimeUtc?.ToString("O") ?? string.Empty,
                        ["JumpListMruOrder"] = order
                    },
                    Confidence = "Medium"
                };

                any = true;
                EventProduced?.Invoke(this, evt);
                order++;
            }

            return Task.FromResult(any);
            }
            catch (IOException ex)
            {
                CollectionWarnings.Add($"JumpList: {Path.GetFileName(filePath)} — {ex.Message}");
                return Task.FromResult(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                CollectionWarnings.Add($"JumpList: {Path.GetFileName(filePath)} — {ex.Message}");
                return Task.FromResult(false);
            }
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private static IEnumerable<byte[]> EnumerateCustomJumpListEntries(byte[] bytes)
    {
        var offset = 0;
        while (offset + 4 <= bytes.Length)
        {
            var size = BitConverter.ToInt32(bytes, offset);
            if (size > 0 && size <= bytes.Length - offset - 4)
            {
                var candidateOffset = offset + 4;
                if (IsLnkHeader(bytes, candidateOffset))
                {
                    var entry = new byte[size];
                    Buffer.BlockCopy(bytes, candidateOffset, entry, 0, size);
                    yield return entry;
                    offset = candidateOffset + size;
                    continue;
                }
            }

            var found = FindLnkHeader(bytes, offset);
            if (found < 0)
                yield break;

            var slice = new byte[bytes.Length - found];
            Buffer.BlockCopy(bytes, found, slice, 0, slice.Length);
            yield return slice;
            offset = found + 4;
        }
    }

    private static int FindLnkHeader(byte[] bytes, int start)
    {
        for (var i = start; i + 20 < bytes.Length; i++)
        {
            if (IsLnkHeader(bytes, i))
                return i;
        }
        return -1;
    }

    private static bool IsLnkHeader(byte[] bytes, int offset)
    {
        if (offset + 20 >= bytes.Length)
            return false;

        if (bytes[offset] != 0x4C || bytes[offset + 1] != 0x00 || bytes[offset + 2] != 0x00 || bytes[offset + 3] != 0x00)
            return false;

        return bytes[offset + 4] == 0x01 &&
               bytes[offset + 5] == 0x14 &&
               bytes[offset + 6] == 0x02 &&
               bytes[offset + 7] == 0x00 &&
               bytes[offset + 8] == 0x00 &&
               bytes[offset + 9] == 0x00 &&
               bytes[offset + 10] == 0x00 &&
               bytes[offset + 11] == 0x00 &&
               bytes[offset + 12] == 0xC0 &&
               bytes[offset + 13] == 0x00 &&
               bytes[offset + 14] == 0x00 &&
               bytes[offset + 15] == 0x00 &&
               bytes[offset + 16] == 0x00 &&
               bytes[offset + 17] == 0x00 &&
               bytes[offset + 18] == 0x00 &&
               bytes[offset + 19] == 0x46;
    }

    private static Dictionary<string, JumpListDestListEntry> BuildDestListMap(RootStorage root)
    {
        var map = new Dictionary<string, JumpListDestListEntry>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryOpenStream("DestList", out var destStream))
            return map;

        using var stream = destStream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var entries = JumpListDestListParser.Parse(bytes);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.StreamName))
            {
                map[entry.StreamName] = entry;
            }
        }

        return map;
    }
}
