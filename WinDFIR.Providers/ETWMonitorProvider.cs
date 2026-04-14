using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml.Linq;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Normalization;
using WinDFIR.Core.Settings;
using ActivityEvent = WinDFIR.Core.Entities.ActivityEvent;

namespace WinDFIR.Providers;

/// <summary>
/// ETW Monitor Provider: Subset of Procmon functionality using ETW-backed event channels.
/// Per specification: ETW Scope (v1) - Process start/stop, File create/write (subset),
/// Registry create/set (subset), Network connect (subset).
/// Kernel callbacks enqueue lightweight captures into a bounded queue; a worker parses and raises <see cref="EventProduced"/>.
/// When the queue is full, events are dropped and counted under <see cref="BurstQueueDropCategory"/> (included in <see cref="GetEtwThrottleStats"/> totals).
/// </summary>
public class ETWMonitorProvider : IProvider, IProcessCreateCacheStatsProvider, IEtwThrottleStatsProvider
{
    public string Name => "ETWMonitorProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;
    private readonly List<EventLogWatcher> _watchers = new();
    private readonly object _watcherLock = new();
    private readonly List<string> _unavailableChannels = new();
    private bool _isPaused;
    private readonly ulong _bootId;
    private readonly object _throttleLock = new();
    private readonly Dictionary<string, (DateTime WindowStartUtc, int Count)> _throttleState = new();
    private readonly Dictionary<string, int> _throttleDrops = new();
    private readonly Dictionary<string, DateTime> _throttleLastReportUtc = new();
    private readonly Dictionary<string, int> _throttleLastReportedDrops = new();
    private readonly Dictionary<string, int> _throttleTotalDrops = new();
    private readonly Dictionary<uint, (DateTime CreateTime, DateTime LastSeenUtc, bool IsProvisional, bool IsLongLived)> _processCreateTimes = new();
    private readonly object _processCreateLock = new();
    private readonly TimeSpan _provisionalTtl;
    private readonly TimeSpan _authoritativeTtl;
    private readonly TimeSpan _longLivedTtl;
    private readonly int _maxEntries;
    private readonly HashSet<string> _longLivedProcesses;
    private readonly int _throttleMaxPerSecond;
    private readonly int _ingestQueueCapacity;
    private BlockingCollection<EtwCapturedRecord>? _ingestQueue;

    private const int DefaultEtwIngestQueueCapacity = 8192;
    internal const string BurstQueueDropCategory = "BurstQueue";

    private const int MaxFileEventsPerSecond = 500;
    private const int MaxRegistryEventsPerSecond = 300;
    private const int MaxNetworkEventsPerSecond = 300;
    private const int ThrottleReportIntervalSeconds = 5;
    private const bool IncludeStack = false;

    public event EventHandler<ActivityEvent>? EventProduced;

    public IReadOnlyList<string> UnavailableChannels => _unavailableChannels.AsReadOnly();

    public ETWMonitorProvider() : this(DefaultEtwIngestQueueCapacity)
    {
    }

    /// <summary>Test seam: smaller bounded queue to exercise burst-drop accounting.</summary>
    internal ETWMonitorProvider(int ingestQueueCapacity)
    {
        _ingestQueueCapacity = ingestQueueCapacity <= 0 ? DefaultEtwIngestQueueCapacity : ingestQueueCapacity;
        _bootId = BootIdProvider.GetBootId();
        var settings = HostWitnessSettings.Load().ProcessCache;
        _provisionalTtl = TimeSpan.FromMinutes(settings.Etw.ProvisionalTtlMinutes);
        _authoritativeTtl = TimeSpan.FromMinutes(settings.Etw.AuthoritativeTtlMinutes);
        _longLivedTtl = TimeSpan.FromMinutes(settings.LongLivedTtlMinutes);
        _maxEntries = settings.Etw.MaxEntries;
        _longLivedProcesses = new HashSet<string>(settings.LongLivedProcessNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        _throttleMaxPerSecond = settings.EtwThrottleMaxPerSecond <= 0 ? 0 : settings.EtwThrottleMaxPerSecond;
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set => _isPaused = value;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isPaused = false;
        _monitoringTask = Task.Run(() => MonitorEtw(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource?.Cancel();

        if (_monitoringTask != null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
        }
    }

    public void Pause()
    {
        _isPaused = true;
    }

    public void Resume()
    {
        _isPaused = false;
    }

    private async Task MonitorEtw(CancellationToken cancellationToken)
    {
        var ingestQueue = new BlockingCollection<EtwCapturedRecord>(_ingestQueueCapacity);
        _ingestQueue = ingestQueue;
        // Ingest runs until CompleteAdding + queue drained; do not cancel mid-drain (avoids silent loss of accepted captures).
        var ingestTask = Task.Run(() => IngestEtwQueue(ingestQueue));
        try
        {
            StartWatchers();

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
        catch
        {
            // ETW access may require elevated privileges
        }
        finally
        {
            StopWatchers();
            try
            {
                ingestQueue.CompleteAdding();
            }
            catch
            {
                // Ignore double-complete or disposed
            }

            try
            {
                await ingestTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
            catch
            {
                // Drain failures should not prevent shutdown
            }

            _ingestQueue = null;
        }
    }

    private void IngestEtwQueue(BlockingCollection<EtwCapturedRecord> queue)
    {
        try
        {
            foreach (var cap in queue.GetConsumingEnumerable())
            {
                try
                {
                    ProcessCapturedRecord(cap);
                }
                catch
                {
                    // Malformed capture; skip without stalling the ingest loop
                }
            }
        }
        catch
        {
            // Queue completed, disposed, or enumerator fault during shutdown
        }
    }

    private void RecordBurstQueueDrop()
    {
        var nowUtc = DateTime.UtcNow;
        var emitCount = 0;
        lock (_throttleLock)
        {
            _throttleTotalDrops[BurstQueueDropCategory] =
                _throttleTotalDrops.TryGetValue(BurstQueueDropCategory, out var t) ? t + 1 : 1;
            _throttleDrops[BurstQueueDropCategory] =
                _throttleDrops.TryGetValue(BurstQueueDropCategory, out var p) ? p + 1 : 1;

            if (_throttleDrops.TryGetValue(BurstQueueDropCategory, out var pending) && pending > 0 &&
                (!_throttleLastReportUtc.TryGetValue(BurstQueueDropCategory, out var lastReport) ||
                 (nowUtc - lastReport).TotalSeconds >= ThrottleReportIntervalSeconds))
            {
                emitCount = pending;
                _throttleLastReportUtc[BurstQueueDropCategory] = nowUtc;
                _throttleLastReportedDrops[BurstQueueDropCategory] = pending;
                _throttleDrops[BurstQueueDropCategory] = 0;
            }
        }

        if (emitCount > 0)
            EmitBurstQueueWarning(emitCount, nowUtc);
    }

    private void EmitBurstQueueWarning(int dropped, DateTime nowUtc)
    {
        if (EventProduced is null)
            return;

        var activityEvent = new ActivityEvent
        {
            Category = "System",
            Action = "Query",
            Timestamp = nowUtc,
            Evidence = new List<EvidenceRef>
            {
                new EvidenceRef("ETW", "burst-queue", null, nowUtc)
            },
            Summary =
                $"ETW ingest queue saturated: {dropped} event capture(s) dropped (bounded capacity {_ingestQueueCapacity})",
            Fields = new Dictionary<string, object>
            {
                ["Category"] = BurstQueueDropCategory,
                ["Dropped"] = dropped,
                ["QueueCapacity"] = _ingestQueueCapacity
            },
            Confidence = "Low"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    internal void AddBurstQueueDropsForTest(int count)
    {
        for (var i = 0; i < count; i++)
            RecordBurstQueueDrop();
    }

    /// <summary>Test seam: true once the bounded ingest queue exists and is accepting adds.</summary>
    internal bool IsIngestQueueReadyForTest() =>
        _ingestQueue is { IsAddingCompleted: false };

    /// <summary>Test seam: enqueue a synthetic capture; returns false when queue is full (counts BurstQueue drop).</summary>
    internal bool TryEnqueueCapturedRecordForTest(EtwCapturedRecord cap)
    {
        var q = _ingestQueue;
        if (q is null || q.IsAddingCompleted)
            return false;

        if (!q.TryAdd(cap, millisecondsTimeout: 0))
        {
            RecordBurstQueueDrop();
            return false;
        }

        return true;
    }

    /// <summary>Test seam: clears burst report timing so the next drop can emit a warning immediately.</summary>
    internal void ResetBurstReportGateForTest()
    {
        lock (_throttleLock)
        {
            _throttleLastReportUtc.Remove(BurstQueueDropCategory);
        }
    }

    internal static EtwCapturedRecord CreateSyntheticKernelFileCaptureForTest(int seq = 0)
    {
        var xml =
            "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><EventData></EventData></Event>";
        return new EtwCapturedRecord(
            xml,
            "Microsoft-Windows-Kernel-File/Operational",
            1000 + seq,
            10 + seq,
            DateTime.UtcNow,
            "Create",
            "Information",
            "Create",
            "Microsoft-Windows-Kernel-File");
    }

    private void StartWatchers()
    {
        var channels = new[]
        {
            "Microsoft-Windows-Kernel-Process/Operational",
            "Microsoft-Windows-Kernel-File/Operational",
            "Microsoft-Windows-Kernel-Network/Operational",
            "Microsoft-Windows-Kernel-Registry/Operational"
        };

        foreach (var channel in channels)
        {
            try
            {
                lock (_watcherLock)
                {
                    _unavailableChannels.Remove(channel);
                }

                if (!IsChannelEnabled(channel))
                {
                    lock (_watcherLock)
                    {
                        _unavailableChannels.Add(channel);
                    }
                    continue;
                }

                var query = new EventLogQuery(channel, PathType.LogName)
                {
                    ReverseDirection = false
                };

                var watcher = new EventLogWatcher(query);
                watcher.EventRecordWritten += OnEventRecordWritten;
                watcher.Enabled = true;

                lock (_watcherLock)
                {
                    _watchers.Add(watcher);
                }
            }
            catch
            {
                // Some channels may be missing or disabled
                lock (_watcherLock)
                {
                    _unavailableChannels.Add(channel);
                }
            }
        }

        if (_unavailableChannels.Count > 0)
        {
            EmitChannelWarning();
        }
    }

    private void StopWatchers()
    {
        lock (_watcherLock)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.Enabled = false;
                    watcher.EventRecordWritten -= OnEventRecordWritten;
                    watcher.Dispose();
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            _watchers.Clear();
        }
    }

    private bool IsChannelEnabled(string channel)
    {
        try
        {
            using var config = new EventLogConfiguration(channel);
            return config.IsEnabled;
        }
        catch
        {
            return false;
        }
    }

    private void EmitChannelWarning()
    {
        if (EventProduced is null)
            return;

        var timestamp = DateTime.UtcNow;
        var message = $"ETW channels unavailable: {string.Join(", ", _unavailableChannels)}";

        var activityEvent = new ActivityEvent
        {
            Category = "System",
            Action = "Query",
            Timestamp = timestamp,
            Evidence = new List<EvidenceRef>
            {
                new EvidenceRef("ETW", "channel-check", null, timestamp)
            },
            Summary = message,
            Fields = new Dictionary<string, object>
            {
                ["UnavailableChannels"] = string.Join(";", _unavailableChannels)
            },
            Confidence = "Medium"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    private void OnEventRecordWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (_isPaused || e.EventRecord is null)
            return;

        var queue = _ingestQueue;
        if (queue is null || queue.IsAddingCompleted)
        {
            try
            {
                e.EventRecord?.Dispose();
            }
            catch
            {
                // Ignore dispose failures
            }

            return;
        }

        try
        {
            var record = e.EventRecord;
            var logName = record.LogName ?? record.ProviderName ?? "ETW";
            var opcode = record.OpcodeDisplayName ?? string.Empty;
            var (category, _) = MapCategoryAction(logName, record.Id, opcode);
            if (ShouldThrottle(category))
                return;

            var xml = record.ToXml();
            var cap = new EtwCapturedRecord(
                xml,
                logName,
                record.RecordId ?? -1L,
                record.Id,
                record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow,
                record.OpcodeDisplayName ?? string.Empty,
                record.LevelDisplayName ?? record.Level?.ToString() ?? "Unknown",
                record.TaskDisplayName ?? string.Empty,
                record.ProviderName ?? "Unknown");

            if (!queue.TryAdd(cap, millisecondsTimeout: 0))
                RecordBurstQueueDrop();
        }
        catch
        {
            // Capture failed; event is lost but ETW callback must return quickly
        }
        finally
        {
            try
            {
                e.EventRecord?.Dispose();
            }
            catch
            {
                // Ignore dispose failures
            }
        }
    }

    private void ProcessCapturedRecord(EtwCapturedRecord cap)
    {
        var timestamp = cap.TimeCreatedUtc;
        var logName = cap.LogName;
        var opcode = cap.Opcode;

        var (category, action) = MapCategoryAction(logName, cap.EventId, opcode);

        var fields = new Dictionary<string, object>
        {
            ["EventId"] = cap.EventId,
            ["Provider"] = cap.ProviderName,
            ["LogName"] = logName,
            ["Opcode"] = opcode,
            ["Level"] = cap.Level,
            ["Task"] = cap.Task
        };

        ProcessKey? processKey = null;
        FileKey? fileKey = null;
        RegistryKey? registryKey = null;
        NetworkFlowKey? networkFlowKey = null;

        TryExtractFromXml(cap.Xml, fields, timestamp, ref processKey, ref fileKey, ref registryKey, ref networkFlowKey);
        NormalizeFields(fields, category, opcode);

        var evidence = new List<EvidenceRef>
        {
            new EvidenceRef("ETW", $"{logName}:{cap.RecordId}", null, timestamp)
        };

        var activityEvent = new ActivityEvent
        {
            Category = category,
            Action = action,
            Timestamp = timestamp,
            Evidence = evidence,
            SubjectProcess = processKey,
            ObjectFile = fileKey,
            ObjectRegistry = registryKey,
            ObjectNetworkFlow = networkFlowKey,
            Summary = $"ETW {category}.{action}",
            Fields = fields,
            Confidence = "High"
        };

        EventProduced?.Invoke(this, activityEvent);

        if (category == "Network" && networkFlowKey.HasValue)
        {
            var size = 0L;
            if (TryGetField(fields, "TransferSize", out var sz) && TryParseLong(sz, out var t))
                size = t;
            else if (TryGetField(fields, "Size", out sz) && TryParseLong(sz, out t))
                size = t;
            var op = opcode.AsSpan();
            if (op.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0)
                NetworkStatsAggregator.AddSent(networkFlowKey.Value, size, 1);
            else if (op.IndexOf("Recv", StringComparison.OrdinalIgnoreCase) >= 0 || op.IndexOf("Receive", StringComparison.OrdinalIgnoreCase) >= 0)
                NetworkStatsAggregator.AddRecv(networkFlowKey.Value, size, 1);
        }

        if (category == "Process" && action == "Stop")
        {
            if (processKey.HasValue)
            {
                RemoveProcessCreateTime(processKey.Value.ProcessId);
            }
            else if ((TryGetField(fields, "ProcessId", out var pidValue) ||
                      TryGetField(fields, "ProcessID", out pidValue) ||
                      TryGetField(fields, "PID", out pidValue)) &&
                     TryParsePid(pidValue, out var pid))
            {
                RemoveProcessCreateTime(pid);
            }
        }
    }

    private (string category, string action) MapCategoryAction(string logName, int eventId, string opcode)
    {
        if (logName.Contains("Kernel-Process", StringComparison.OrdinalIgnoreCase))
        {
            return eventId switch
            {
                1 => ("Process", "Start"),
                2 => ("Process", "Stop"),
                _ => ("Process", opcode.Contains("Start", StringComparison.OrdinalIgnoreCase) ? "Start" :
                               opcode.Contains("Stop", StringComparison.OrdinalIgnoreCase) ? "Stop" : "Query")
            };
        }

        if (logName.Contains("Kernel-File", StringComparison.OrdinalIgnoreCase))
        {
            if (opcode.Contains("Create", StringComparison.OrdinalIgnoreCase) ||
                opcode.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
                opcode.Contains("Delete", StringComparison.OrdinalIgnoreCase))
                return ("File", "Write");

            if (opcode.Contains("Read", StringComparison.OrdinalIgnoreCase) ||
                opcode.Contains("Open", StringComparison.OrdinalIgnoreCase))
                return ("File", "Open");

            if (opcode.Contains("Close", StringComparison.OrdinalIgnoreCase))
                return ("File", "Stop");

            return ("File", "Query");
        }

        if (logName.Contains("Kernel-Registry", StringComparison.OrdinalIgnoreCase))
        {
            if (opcode.Contains("Set", StringComparison.OrdinalIgnoreCase) ||
                opcode.Contains("Create", StringComparison.OrdinalIgnoreCase) ||
                opcode.Contains("Delete", StringComparison.OrdinalIgnoreCase))
                return ("Registry", "Write");

            if (opcode.Contains("Open", StringComparison.OrdinalIgnoreCase))
                return ("Registry", "Open");

            if (opcode.Contains("Query", StringComparison.OrdinalIgnoreCase))
                return ("Registry", "Query");

            return ("Registry", "Query");
        }

        if (logName.Contains("Kernel-Network", StringComparison.OrdinalIgnoreCase))
        {
            if (opcode.Contains("Connect", StringComparison.OrdinalIgnoreCase))
                return ("Network", "Connect");

            if (opcode.Contains("Disconnect", StringComparison.OrdinalIgnoreCase))
                return ("Network", "Stop");

            return ("Network", "Query");
        }

        return ("ETW", "Query");
    }

    private void TryExtractFromXml(string xml, Dictionary<string, object> fields, DateTime timestamp,
        ref ProcessKey? processKey, ref FileKey? fileKey, ref RegistryKey? registryKey, ref NetworkFlowKey? networkFlowKey)
    {
        try
        {
            var doc = XDocument.Parse(xml);

            var execution = doc.Descendants("Execution").FirstOrDefault();
            var executionPid = execution?.Attribute("ProcessID")?.Value;
            uint? execPidValue = null;
            if (TryParseUInt(executionPid, out var execPid))
            {
                execPidValue = execPid;
                fields["ExecutionProcessId"] = execPid;
            }

            foreach (var data in doc.Descendants("EventData").Descendants("Data"))
            {
                var name = data.Attribute("Name")?.Value;
                var value = data.Value ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(name))
                    fields[name] = value;
                else
                    fields[$"EventData_{fields.Count}"] = value;
            }

            if (processKey is null)
            {
                if ((TryGetField(fields, "ProcessId", out var pidValue) ||
                     TryGetField(fields, "ProcessID", out pidValue) ||
                     TryGetField(fields, "PID", out pidValue)) &&
                    TryParsePid(pidValue, out var pid))
                {
                    processKey = ResolveProcessKey(pid, timestamp, fields);
                }
                else if (execPidValue.HasValue)
                {
                    processKey = ResolveProcessKey(execPidValue.Value, timestamp, fields);
                }
            }

            var filePath = FirstField(fields, "FileName", "FilePath", "TargetFilename", "Path");
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                fileKey = KeyGenerator.GenerateFileKey(null, null, filePath, null);
                fields["Path"] = filePath;
            }

            var regPath = FirstField(fields, "KeyName", "ObjectName", "RegistryPath", "KeyPath");
            if (!string.IsNullOrWhiteSpace(regPath))
            {
                registryKey = KeyGenerator.GenerateRegistryKey(regPath);
                fields["Path"] = regPath;
            }

            if (TryBuildEndpoints(fields, out var localEp, out var remoteEp, out var protocol))
            {
                var timeBucket = timestamp.Date.AddHours(timestamp.Hour);
                networkFlowKey = KeyGenerator.GenerateNetworkFlowKey(protocol, localEp, remoteEp, processKey?.ProcessId, timeBucket);
                fields["LocalEndpoint"] = localEp;
                fields["RemoteEndpoint"] = remoteEp;
                fields["Protocol"] = protocol;
            }
        }
        catch
        {
            // Ignore XML parsing errors
        }
    }

    private ProcessKey ResolveProcessKey(uint pid, DateTime eventTimestamp, Dictionary<string, object> fields)
    {
        var createTime = TryGetProcessCreateTime(fields);
        if (createTime.HasValue)
        {
            CacheProcessCreateTime(pid, createTime.Value, eventTimestamp, false, IsLongLivedProcess(fields));
            return KeyGenerator.GenerateProcessKey(_bootId, pid, createTime.Value);
        }

        var cached = GetCachedProcessCreateTime(pid, eventTimestamp);
        if (cached.HasValue)
            return KeyGenerator.GenerateProcessKey(_bootId, pid, cached.Value);

        CacheProcessCreateTime(pid, eventTimestamp, eventTimestamp, true, IsLongLivedProcess(fields));
        return KeyGenerator.GenerateProcessKey(_bootId, pid, eventTimestamp);
    }

    private DateTime? GetCachedProcessCreateTime(uint pid, DateTime eventTimestamp)
    {
        lock (_processCreateLock)
        {
            if (_processCreateTimes.TryGetValue(pid, out var cached))
            {
                var ttl = cached.IsLongLived
                    ? _longLivedTtl
                    : cached.IsProvisional ? _provisionalTtl : _authoritativeTtl;
                if (eventTimestamp - cached.LastSeenUtc > ttl)
                {
                    _processCreateTimes.Remove(pid);
                    return null;
                }

                _processCreateTimes[pid] = (cached.CreateTime, eventTimestamp, cached.IsProvisional, cached.IsLongLived);
                return cached.CreateTime;
            }
        }

        return null;
    }

    private void CacheProcessCreateTime(uint pid, DateTime createTime, DateTime eventTimestamp, bool isProvisional, bool isLongLived)
    {
        lock (_processCreateLock)
        {
            _processCreateTimes[pid] = (createTime, eventTimestamp, isProvisional, isLongLived);
            EnforceCacheLimit();
        }
    }

    private void RemoveProcessCreateTime(uint pid)
    {
        lock (_processCreateLock)
        {
            _processCreateTimes.Remove(pid);
        }
    }

    private void EnforceCacheLimit()
    {
        if (_maxEntries <= 0 || _processCreateTimes.Count <= _maxEntries)
            return;

        var removeCount = _processCreateTimes.Count - _maxEntries;
        foreach (var entry in _processCreateTimes.OrderBy(kvp => kvp.Value.LastSeenUtc).Take(removeCount).ToList())
        {
            _processCreateTimes.Remove(entry.Key);
        }
    }

    private bool IsLongLivedProcess(Dictionary<string, object> fields)
    {
        if (_longLivedProcesses.Count == 0)
            return false;

        var imageName = FirstField(fields, "ImageName", "ProcessName");
        if (!string.IsNullOrWhiteSpace(imageName))
        {
            var name = Path.GetFileName(imageName);
            return _longLivedProcesses.Contains(name);
        }

        return false;
    }

    public ProcessCreateCacheStats GetProcessCreateCacheStats()
    {
        lock (_processCreateLock)
        {
            var total = _processCreateTimes.Count;
            var provisional = _processCreateTimes.Count(kvp => kvp.Value.IsProvisional);
            var longLived = _processCreateTimes.Count(kvp => kvp.Value.IsLongLived);
            return new ProcessCreateCacheStats
            {
                ProviderName = Name,
                TotalEntries = total,
                ProvisionalEntries = provisional,
                LongLivedEntries = longLived,
                MaxEntries = _maxEntries,
                ProvisionalTtl = _provisionalTtl,
                AuthoritativeTtl = _authoritativeTtl,
                LongLivedTtl = _longLivedTtl
            };
        }
    }

    internal ProcessKey ResolveProcessKeyForTest(uint pid, DateTime eventTimestamp, Dictionary<string, object> fields)
    {
        return ResolveProcessKey(pid, eventTimestamp, fields);
    }

    internal void ClearProcessCreateCacheForTest()
    {
        lock (_processCreateLock)
        {
            _processCreateTimes.Clear();
        }
    }

    private static DateTime? TryGetProcessCreateTime(Dictionary<string, object> fields)
    {
        var keys = new[]
        {
            "ProcessStartTime",
            "ProcessStartTimeUtc",
            "ProcessCreationTime",
            "StartTime",
            "CreateTime",
            "CreationUtcTime"
        };

        foreach (var key in keys)
        {
            if (TryGetField(fields, key, out var value))
            {
                var parsed = TryParseTimestamp(value);
                if (parsed.HasValue)
                    return parsed.Value;
            }
        }

        return null;
    }

    private static DateTime? TryParseTimestamp(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (long.TryParse(value, out var numeric))
        {
            if (numeric > 116444736000000000L)
                return TimeNormalizer.FromFileTime((ulong)numeric);

            if (numeric >= 1000000000000L && numeric <= 32503680000000L)
                return TimeNormalizer.FromUnixTimeMilliseconds(numeric);

            if (numeric >= 1000000000L && numeric <= 32503680000L)
                return TimeNormalizer.FromUnixTimeSeconds(numeric);
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return TimeNormalizer.NormalizeToUtc(parsed);

        return null;
    }

    private void NormalizeFields(Dictionary<string, object> fields, string category, string opcode)
    {
        if (!fields.ContainsKey("Operation") && !string.IsNullOrWhiteSpace(opcode))
            fields["Operation"] = opcode;

        if (!fields.ContainsKey("Result"))
        {
            var result = FirstField(fields, "Status", "Result", "NTStatus", "Outcome");
            if (!string.IsNullOrWhiteSpace(result))
                fields["Result"] = result;
        }

        if (!fields.ContainsKey("Duration"))
        {
            var duration = FirstField(fields, "Duration", "ElapsedTime", "Latency");
            if (!string.IsNullOrWhiteSpace(duration))
                fields["Duration"] = duration;
        }

        if (!fields.ContainsKey("ProcessName"))
        {
            var imageName = FirstField(fields, "ImageName", "ProcessName");
            if (!string.IsNullOrWhiteSpace(imageName))
                fields["ProcessName"] = imageName;
        }

        if (!fields.ContainsKey("ParentProcessId"))
        {
            var parentPid = FirstField(fields, "ParentProcessId", "ParentPID");
            if (!string.IsNullOrWhiteSpace(parentPid))
                fields["ParentProcessId"] = parentPid;
        }

        if (IncludeStack && !fields.ContainsKey("Stack"))
        {
            var stack = FirstField(fields, "Stack", "CallStack");
            if (!string.IsNullOrWhiteSpace(stack))
                fields["Stack"] = stack;
        }
    }

    private bool ShouldThrottle(string category)
    {
        var nowUtc = DateTime.UtcNow;
        var limit = GetThrottleLimitForCategory(category);

        if (limit <= 0)
            return false;

        lock (_throttleLock)
        {
            if (!_throttleState.TryGetValue(category, out var state) ||
                (nowUtc - state.WindowStartUtc).TotalSeconds >= 1)
            {
                ReportThrottleDrops(category, limit, nowUtc);
                _throttleState[category] = (nowUtc, 1);
                return false;
            }

            state.Count++;
            _throttleState[category] = state;
            if (state.Count > limit)
            {
                _throttleDrops[category] = _throttleDrops.TryGetValue(category, out var dropped)
                    ? dropped + 1
                    : 1;
                _throttleTotalDrops[category] = _throttleTotalDrops.TryGetValue(category, out var total)
                    ? total + 1
                    : 1;
                return true;
            }

            return false;
        }
    }

    private int GetThrottleLimitForCategory(string category)
    {
        if (_throttleMaxPerSecond > 0)
            return _throttleMaxPerSecond;
        return category switch
        {
            "File" => MaxFileEventsPerSecond,
            "Registry" => MaxRegistryEventsPerSecond,
            "Network" => MaxNetworkEventsPerSecond,
            _ => 0
        };
    }

    private void ReportThrottleDrops(string category, int limit, DateTime nowUtc)
    {
        if (!_throttleDrops.TryGetValue(category, out var dropped) || dropped <= 0)
            return;

        if (_throttleLastReportUtc.TryGetValue(category, out var lastReport) &&
            (nowUtc - lastReport).TotalSeconds < ThrottleReportIntervalSeconds)
            return;

        _throttleLastReportUtc[category] = nowUtc;
        _throttleLastReportedDrops[category] = dropped;
        _throttleDrops[category] = 0;

        EmitThrottleWarning(category, limit, dropped, nowUtc);
    }

    private void EmitThrottleWarning(string category, int limit, int dropped, DateTime nowUtc)
    {
        if (EventProduced is null)
            return;

        var activityEvent = new ActivityEvent
        {
            Category = "System",
            Action = "Query",
            Timestamp = nowUtc,
            Evidence = new List<EvidenceRef>
            {
                new EvidenceRef("ETW", "throttle", null, nowUtc)
            },
            Summary = $"ETW throttled {dropped} {category} events (limit {limit}/s)",
            Fields = new Dictionary<string, object>
            {
                ["Category"] = category,
                ["Dropped"] = dropped,
                ["LimitPerSecond"] = limit
            },
            Confidence = "Low"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    public EtwThrottleStats GetEtwThrottleStats()
    {
        lock (_throttleLock)
        {
            var lastReportUtc = _throttleLastReportUtc.Count > 0
                ? _throttleLastReportUtc.Values.Max()
                : DateTime.MinValue;

            return new EtwThrottleStats
            {
                LastReportedDrops = new Dictionary<string, int>(_throttleLastReportedDrops),
                TotalDrops = new Dictionary<string, int>(_throttleTotalDrops),
                LastReportUtc = lastReportUtc
            };
        }
    }

    private static bool TryBuildEndpoints(Dictionary<string, object> fields, out string localEndpoint, out string remoteEndpoint, out string protocol)
    {
        localEndpoint = string.Empty;
        remoteEndpoint = string.Empty;
        protocol = "TCP";

        var localAddr = FirstField(fields, "LocalAddress", "SourceAddress", "saddr");
        var remoteAddr = FirstField(fields, "RemoteAddress", "DestAddress", "daddr");
        var localPortValue = FirstField(fields, "LocalPort", "SourcePort", "sport");
        var remotePortValue = FirstField(fields, "RemotePort", "DestPort", "dport");
        var protoValue = FirstField(fields, "Protocol", "ProtocolName");

        if (!string.IsNullOrWhiteSpace(protoValue))
            protocol = protoValue;

        if (string.IsNullOrWhiteSpace(localAddr) || string.IsNullOrWhiteSpace(remoteAddr))
            return false;

        if (!TryParsePort(localPortValue, out var localPort) || !TryParsePort(remotePortValue, out var remotePort))
            return false;

        localEndpoint = NormalizeEndpoint(localAddr, localPort);
        remoteEndpoint = NormalizeEndpoint(remoteAddr, remotePort);
        return true;
    }

    private static string? FirstField(Dictionary<string, object> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetField(fields, name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool TryGetField(Dictionary<string, object> fields, string name, out string value)
    {
        if (fields.TryGetValue(name, out var raw) && raw != null)
        {
            value = raw.ToString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseLong(string? value, out long result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseUInt(string? value, out uint result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = 0;
            return false;
        }

        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParsePid(string value, out uint result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

        return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParsePort(string? value, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out port);

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
    }

    private static string NormalizeEndpoint(string address, int port)
    {
        if (IPAddress.TryParse(address, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return $"[{address}]:{port}";
        }

        return $"{address}:{port}";
    }

    internal readonly record struct EtwCapturedRecord(
        string Xml,
        string LogName,
        long RecordId,
        int EventId,
        DateTime TimeCreatedUtc,
        string Opcode,
        string Level,
        string Task,
        string ProviderName);
}
