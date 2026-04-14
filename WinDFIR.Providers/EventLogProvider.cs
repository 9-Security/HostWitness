using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using WinDFIR.Core.Settings;
using ActivityEvent = WinDFIR.Core.Entities.ActivityEvent;

namespace WinDFIR.Providers;

/// <summary>
/// Event log provider: reads classic logs (Security/System/Application) plus common IR-focused
/// operational channels when present (PowerShell, WMI-Activity, Task Scheduler, Defender, Sysmon).
/// Missing, disabled, or inaccessible channels are skipped without failing the provider.
/// </summary>
public class EventLogProvider : IProvider, IProcessCreateCacheStatsProvider
{
    public string Name => "EventLogProvider";

    private const int MaxEventsPerLog = 20000;
    private const int MaxPropertyFields = 20;

    private const string LogPowerShellOperational = "Microsoft-Windows-PowerShell/Operational";
    private const string LogWmiActivityOperational = "Microsoft-Windows-WMI-Activity/Operational";
    private const string LogTaskSchedulerOperational = "Microsoft-Windows-TaskScheduler/Operational";
    private const string LogDefenderOperational = "Microsoft-Windows-Windows Defender/Operational";
    private const string LogSysmonOperationalPrimary = "Microsoft-Windows-Sysmon/Operational";
    private const string LogSysmonOperationalLegacy = "Sysmon/Operational";

    private static readonly string[][] IrHighValueLogChannels =
    {
        new[] { LogPowerShellOperational },
        new[] { LogWmiActivityOperational },
        new[] { LogTaskSchedulerOperational },
        new[] { LogDefenderOperational },
        new[] { LogSysmonOperationalPrimary, LogSysmonOperationalLegacy }
    };

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _processingTask;
    private readonly ulong _bootId;
    private readonly Dictionary<uint, (DateTime CreateTime, DateTime LastSeenUtc, bool IsProvisional, bool IsLongLived)> _processCreateTimes = new();
    private readonly object _processCreateLock = new();
    private readonly TimeSpan _provisionalTtl;
    private readonly TimeSpan _authoritativeTtl;
    private readonly TimeSpan _longLivedTtl;
    private readonly int _maxEntries;
    private readonly HashSet<string> _longLivedProcesses;

    public event EventHandler<ActivityEvent>? EventProduced;

    /// <summary>
    /// Test seam: when set, used instead of constructing <see cref="EventLogReader"/> directly.
    /// Return <c>null</c> to simulate an unavailable log; throw <see cref="UnauthorizedAccessException"/>,
    /// <see cref="EventLogNotFoundException"/>, or <see cref="EventLogReadingException"/> to exercise degrade paths.
    /// </summary>
    internal Func<string, EventLogReader?>? CreateEventLogReaderForTest { get; set; }

    public EventLogProvider()
    {
        _bootId = BootIdProvider.GetBootId();
        var settings = HostWitnessSettings.Load().ProcessCache;
        _provisionalTtl = TimeSpan.FromMinutes(settings.EventLog.ProvisionalTtlMinutes);
        _authoritativeTtl = TimeSpan.FromMinutes(settings.EventLog.AuthoritativeTtlMinutes);
        _longLivedTtl = TimeSpan.FromMinutes(settings.LongLivedTtlMinutes);
        _maxEntries = settings.EventLog.MaxEntries;
        _longLivedProcesses = new HashSet<string>(settings.LongLivedProcessNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_processingTask != null && !_processingTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingTask = Task.Run(() => ProcessEventLogs(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

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

    private async Task ProcessEventLogs(CancellationToken cancellationToken)
    {
        var logNames = new[] { "Security", "System", "Application" };

        foreach (var logName in logNames)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            await ProcessEventLogIfAvailable(logName, cancellationToken);
        }

        foreach (var channelGroup in IrHighValueLogChannels)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            foreach (var logName in channelGroup)
            {
                if (await ProcessEventLogIfAvailable(logName, cancellationToken))
                    break;
            }
        }
    }

    /// <summary>
    /// Returns true if the log was opened and read (possibly zero events); false if unavailable or failed to open.
    /// </summary>
    private async Task<bool> ProcessEventLogIfAvailable(string logName, CancellationToken cancellationToken)
    {
        try
        {
            EventLogReader? reader;
            if (CreateEventLogReaderForTest != null)
            {
                reader = CreateEventLogReaderForTest(logName);
                if (reader is null)
                    return false;
            }
            else
            {
                reader = new EventLogReader(logName, PathType.LogName);
            }

            using (reader)
            {
                EventRecord? record;
                var processed = 0;

                while ((record = reader.ReadEvent()) != null && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        ProcessEventRecord(record, logName);
                        processed++;
                        if (processed >= MaxEventsPerLog)
                            break;
                    }
                    catch
                    {
                        // Skip records we can't process
                    }
                    finally
                    {
                        record.Dispose();
                    }

                    await Task.Yield(); // Allow cancellation
                }

                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Event log access may require admin privileges
        }
        catch (EventLogNotFoundException)
        {
            // Channel not installed or name differs by OS / locale
        }
        catch (EventLogReadingException)
        {
            // Corrupt or unreadable log
        }
        catch
        {
            // Other errors (invalid name, provider not registered, etc.)
        }

        return false;
    }

    private void ProcessEventRecord(EventRecord record, string logName)
    {
        var timestamp = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow;
        var eventId = record.Id;
        var level = record.LevelDisplayName ?? record.Level?.ToString() ?? "Unknown";
        var source = record.ProviderName ?? "Unknown";

        // Map common event IDs to categories and actions
        var (category, action) = MapEventToCategoryAction(eventId, logName);

        var evidence = new List<EvidenceRef>
        {
            new EvidenceRef(
                "EventLog",
                $"{logName}:{record.RecordId}",
                null,
                timestamp)
        };

        var fields = new Dictionary<string, object>
        {
            ["EventId"] = eventId,
            ["Level"] = level,
            ["Source"] = source,
            ["LogName"] = logName,
            ["RecordId"] = record.RecordId ?? 0,
            ["MachineName"] = record.MachineName ?? Environment.MachineName
        };

        // Extract process information from event properties
        ProcessKey? processKey = null;
        UserKey? userKey = null;

        if (record.Properties != null)
        {
            var limit = Math.Min(record.Properties.Count, MaxPropertyFields);
            for (var i = 0; i < limit; i++)
            {
                var prop = record.Properties[i];
                var propValue = prop.Value?.ToString() ?? string.Empty;
                fields[$"Property_{i}"] = propValue;

                if (userKey is null && propValue.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                {
                    userKey = TryCreateUserKey(propValue);
                }
            }
        }

        TryExtractFromXml(record, timestamp, fields, ref processKey, ref userKey);

        // Special handling for Security log Event ID 4688 (Process Creation)
        if (logName == "Security" && eventId == 4688)
        {
            category = "Process";
            action = "Start";
            // Would extract ProcessKey from event data
        }

        var activityEvent = new ActivityEvent
        {
            Category = category,
            Action = action,
            Timestamp = timestamp,
            Evidence = evidence,
            SubjectProcess = processKey,
            SubjectUser = userKey,
            Summary = $"{logName} Event {eventId}: {level} from {source}",
            Fields = fields,
            Confidence = logName == "Security" ? "High" : "Medium"
        };

        EventProduced?.Invoke(this, activityEvent);

        if (category == "Process" && action == "Stop")
        {
            if (processKey.HasValue)
            {
                RemoveProcessCreateTime(processKey.Value.ProcessId);
            }
            else if ((TryGetField(fields, "ProcessId", out var pidValue) ||
                      TryGetField(fields, "ProcessID", out pidValue) ||
                      TryGetField(fields, "NewProcessId", out pidValue)) &&
                     TryParsePid(pidValue, out var pid))
            {
                RemoveProcessCreateTime(pid);
            }
        }
    }

    private static (string category, string action) MapEventToCategoryAction(int eventId, string logName)
    {
        return logName switch
        {
            "Security" => eventId switch
            {
                4624 => ("Logon", "Start"),
                4625 => ("Logon", "Failed"),
                4634 => ("Logon", "Stop"),
                4648 => ("Logon", "Start"),
                4672 => ("Logon", "Start"),
                4688 => ("Process", "Start"),
                4689 => ("Process", "Stop"),
                // 4697 service install / 4698–4701 scheduled-task audit: avoid mislabeling as service runtime Start/Stop.
                4697 => ("Service", "Query"),
                4698 or 4699 or 4700 or 4701 => ("ScheduledTask", "Query"),
                _ => ("Security", "Query")
            },
            "System" => eventId switch
            {
                6005 => ("System", "Start"),
                6006 => ("System", "Stop"),
                6008 => ("System", "Stop"),
                6009 => ("System", "Start"),
                1074 => ("System", "Stop"),
                _ => ("System", "Query")
            },
            "Application" => ("Application", "Query"),
            LogPowerShellOperational => eventId switch
            {
                4103 => ("PowerShell", "Module"),
                4104 => ("PowerShell", "ScriptBlock"),
                _ => ("PowerShell", "Query")
            },
            LogWmiActivityOperational => ("WMI", "Query"),
            LogTaskSchedulerOperational => eventId switch
            {
                106 or 129 => ("ScheduledTask", "Register"),
                140 => ("ScheduledTask", "Modify"),
                141 or 102 => ("ScheduledTask", "Delete"),
                200 => ("ScheduledTask", "Start"),
                201 => ("ScheduledTask", "Stop"),
                _ => ("ScheduledTask", "Query")
            },
            LogDefenderOperational => eventId switch
            {
                1116 or 1118 => ("Antimalware", "Detection"),
                1117 => ("Antimalware", "Action"),
                5007 => ("Antimalware", "ConfigChange"),
                _ => ("Antimalware", "Query")
            },
            LogSysmonOperationalPrimary or LogSysmonOperationalLegacy => eventId switch
            {
                1 => ("Process", "Start"),
                5 => ("Process", "Stop"),
                3 => ("Network", "Query"),
                7 => ("Image", "Load"),
                11 => ("File", "Create"),
                22 => ("DNS", "Query"),
                23 => ("File", "Delete"),
                _ => ("Sysmon", "Query")
            },
            _ => ("Log", "Query")
        };
    }

    internal static (string Category, string Action) MapEventToCategoryActionForTest(int eventId, string logName) =>
        MapEventToCategoryAction(eventId, logName);

    private void TryExtractFromXml(EventRecord record, DateTime timestamp, Dictionary<string, object> fields,
        ref ProcessKey? processKey, ref UserKey? userKey)
    {
        try
        {
            var xml = record.ToXml();
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
                if (TryGetField(fields, "NewProcessId", out var pidValue) && TryParsePid(pidValue, out var pid))
                {
                    processKey = ResolveProcessKey(pid, timestamp, fields);
                }
                else if ((TryGetField(fields, "ProcessId", out pidValue) ||
                          TryGetField(fields, "ProcessID", out pidValue)) &&
                         TryParsePid(pidValue, out pid))
                {
                    processKey = ResolveProcessKey(pid, timestamp, fields);
                }
                else if (execPidValue.HasValue)
                {
                    processKey = ResolveProcessKey(execPidValue.Value, timestamp, fields);
                }
            }

            if (userKey is null)
            {
                var sid = TryGetSid(fields, "SubjectUserSid")
                          ?? TryGetSid(fields, "TargetUserSid")
                          ?? TryGetSid(fields, "LogonGuid");
                if (!string.IsNullOrWhiteSpace(sid))
                    userKey = TryCreateUserKey(sid);
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

        if (TryGetField(fields, "ProcessName", out var processName) ||
            TryGetField(fields, "ImageName", out processName))
        {
            var name = Path.GetFileName(processName);
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
            "CreationUtcTime",
            "ProcessStartTime",
            "ProcessStartTimeUtc",
            "ProcessCreationTime",
            "StartTime",
            "CreateTime"
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

    private static string? TryGetSid(Dictionary<string, object> fields, string name)
    {
        if (TryGetField(fields, name, out var value) && value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            return value;
        return null;
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
        {
            return uint.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
        }

        return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static UserKey? TryCreateUserKey(string sid)
    {
        try
        {
            return KeyGenerator.GenerateUserKey(sid);
        }
        catch
        {
            return null;
        }
    }

}
