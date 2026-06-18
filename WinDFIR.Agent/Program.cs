using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WinDFIR.Core.Analysis;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Normalization;
using WinDFIR.Core.Repository;
using WinDFIR.Core.Settings;
using WinDFIR.Core.Snapshot;
using WinDFIR.Providers;

namespace WinDFIR.Agent;

/// <summary>
/// Headless agent: runs the same collectors as the UI app and exports a snapshot to disk.
/// For remote deployment: copy HostWitness.Agent.exe to target, run with output path, copy back the snapshot folder.
/// Optionally pass --repo=&lt;target&gt; to publish the finished bundle into a central case repository so
/// collections from many investigated hosts gather in one place. The target is either a filesystem path
/// (shared folder / mounted bucket) or an http(s):// URL of an evidence-intake server. For an HTTP intake
/// that requires authentication, pass --repo-token=&lt;shared-secret&gt; (sent as a bearer token).
///
/// Exit codes: 0 = success, 1 = export/general failure, 2 = provider stop failure, 3 = repository publish failure.
/// </summary>
internal static class Program
{
    private const int DefaultCollectSeconds = 30;

    public static async Task<int> Main(string[] args)
    {
        try
        {
        var (outputDir, collectSeconds, enableEtw, providerFilter, evtxFiles, srumFiles, bitsFiles, wmiFiles, repoPath, repoToken) = ParseArgs(args);

        if (string.IsNullOrEmpty(outputDir))
        {
            outputDir = Path.Combine(Environment.CurrentDirectory, "AgentOutput");
            Console.WriteLine($"No output path given; using: {outputDir}");
        }

        Directory.CreateDirectory(outputDir);

        HostWitnessSettings.EnsureSettingsFile();
        var settings = HostWitnessSettings.Load();
        var maxEvents = settings.Index?.MaxEvents ?? 200_000;
        var index = new InMemoryActivityIndex(maxEvents == 0 ? 0 : maxEvents);
        var exporter = new SnapshotExporter { UseVssSnapshots = true };

        var providers = BuildProviders(settings, enableEtw, providerFilter, evtxFiles, srumFiles, bitsFiles, wmiFiles);

        using var cts = new CancellationTokenSource();
        foreach (var p in providers)
        {
            p.EventProduced += (_, evt) =>
            {
                var normalized = ActivityEventNormalizer.Normalize(evt);
                index.AddEvent(normalized);
            };
        }

        Console.WriteLine("Starting providers...");
        try
        {
            await ProviderLifecycleHelper.StartProvidersAsync(providers, cts.Token);
        }
        catch (ProviderStartException ex)
        {
            Console.Error.WriteLine($"Provider start failed: {ex.StartException.Message}");
            foreach (var stopEx in ex.StopExceptions)
                Console.Error.WriteLine($"Provider rollback stop failed: {stopEx.Message}");
            return 1;
        }

        Console.WriteLine($"Collecting for {collectSeconds} seconds...");
        await Task.Delay(TimeSpan.FromSeconds(collectSeconds), cts.Token);

        Console.WriteLine("Stopping providers...");
        cts.Cancel();
        var stopExceptions = await ProviderLifecycleHelper.StopProvidersAsync(providers, CancellationToken.None);
        if (stopExceptions.Count > 0)
        {
            foreach (var ex in stopExceptions)
                Console.Error.WriteLine($"Provider stop failed: {ex.Message}");
            Console.Error.WriteLine("Export skipped because one or more providers failed to stop cleanly.");
            return 2;
        }

        // Cross-source anomaly check (P6): compare the live API view against the raw/offline view and fold any
        // discrepancies into the snapshot as advisory indicators. No-op when a source pair is absent.
        try
        {
            var anomalies = new List<ActivityEvent>();
            anomalies.AddRange(CrossSourceServiceAnalyzer.Analyze(index));
            anomalies.AddRange(CrossSourceTaskAnalyzer.Analyze(index));
            anomalies.AddRange(CrossSourceRunKeyAnalyzer.Analyze(index));
            foreach (var a in anomalies)
                index.AddEvent(ActivityEventNormalizer.Normalize(a));
            if (anomalies.Count > 0)
                Console.WriteLine($"Cross-source anomalies (live vs offline): {anomalies.Count} (advisory — review).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cross-source analysis skipped: {ex.Message}");
        }

        Console.WriteLine($"Exporting snapshot to {outputDir} (events: {index.EventCount})...");
        string bundlePath;
        try
        {
            var manifestExtras = CollectionMetadataBuilder.BuildBaseManifestExtras(
                settings,
                executionContext: "agent_headless",
                useVssSnapshots: exporter.UseVssSnapshots,
                enabledProviders: providers.Select(p => p.GetType().Name),
                collectSeconds: collectSeconds);
            manifestExtras["toolVersion"] = ToolVersionProvider.GetCurrentVersion(typeof(Program));

            var exportOptions = new SnapshotExportOptions
            {
                ManifestExtras = manifestExtras,
                SourceEventCount = index.EventCount
            };
            bundlePath = await exporter.ExportAsync(index, outputDir, exportOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Export failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Done. Snapshot folder: " + bundlePath);

        // Optional: publish the finished, hash-verified bundle into a central case repository so an analyst
        // can gather collections from many investigated hosts in one shared location. Export already
        // succeeded above, so a publish failure is reported distinctly (exit 3) — the local bundle is intact.
        if (!string.IsNullOrEmpty(repoPath))
        {
            HttpClient? httpClient = null;
            try
            {
                // A URL routes to the HTTP evidence intake; any other value is a filesystem path (local dir,
                // UNC share, or mounted bucket). The factory owns the rule; httpClient is non-null only for HTTP.
                var sink = ArtifactSinkFactory.Create(repoPath, repoToken, out httpClient);

                Console.WriteLine($"Publishing bundle to {sink.Describe()}...");
                var result = await sink.PublishBundleAsync(bundlePath);
                switch (result.Status)
                {
                    case BundlePublishStatus.Published:
                        Console.WriteLine($"Published to {result.DestinationPath} " +
                            $"({result.FilesCopied} copied, {result.FilesSkipped} already present, {result.BytesCopied} bytes).");
                        break;
                    case BundlePublishStatus.AlreadyPresent:
                        Console.WriteLine($"Already present in repository (collectionId {result.CollectionId}); nothing to do.");
                        break;
                    default:
                        Console.Error.WriteLine("Publish failed:");
                        foreach (var issue in result.Issues)
                            Console.Error.WriteLine($"  - {issue}");
                        return 3;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Publish failed: {ex.Message}");
                return 3;
            }
            finally
            {
                httpClient?.Dispose();
            }
        }

        return 0;
        }
        catch (Exception ex)
        {
            // Keep the documented exit-code contract authoritative: any unexpected failure (bad path,
            // settings load, provider construction, etc.) maps to 1 with a one-line stderr message
            // rather than an undocumented runtime exit code plus a raw stack trace.
            Console.Error.WriteLine($"Agent failed: {ex.Message}");
            return 1;
        }
    }

    private static (string? outputDir, int collectSeconds, bool enableEtw, HashSet<string>? providerFilter, List<string> evtxFiles, List<string> srumFiles, List<string> bitsFiles, List<string> wmiFiles, string? repoPath, string? repoToken) ParseArgs(string[] args)
    {
        string? outputDir = null;
        var collectSeconds = DefaultCollectSeconds;
        var enableEtw = false;
        HashSet<string>? providerFilter = null;
        var evtxFiles = new List<string>();
        var srumFiles = new List<string>();
        var bitsFiles = new List<string>();
        var wmiFiles = new List<string>();
        string? repoPath = null;
        string? repoToken = null;

        var positional = new List<string>();
        foreach (var a in args)
        {
            var arg = a.Trim();
            if (string.IsNullOrEmpty(arg)) continue;
            if (arg.Equals("--etw", StringComparison.OrdinalIgnoreCase))
            {
                enableEtw = true;
                continue;
            }
            if (arg.StartsWith("--providers=", StringComparison.OrdinalIgnoreCase))
            {
                var list = arg.Substring("--providers=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                providerFilter = new HashSet<string>(list.Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (arg.StartsWith("--evtx=", StringComparison.OrdinalIgnoreCase))
            {
                var list = arg.Substring("--evtx=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                evtxFiles.AddRange(list);
                continue;
            }
            if (arg.StartsWith("--srum=", StringComparison.OrdinalIgnoreCase))
            {
                var list = arg.Substring("--srum=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                srumFiles.AddRange(list);
                continue;
            }
            if (arg.StartsWith("--bits=", StringComparison.OrdinalIgnoreCase))
            {
                var list = arg.Substring("--bits=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bitsFiles.AddRange(list);
                continue;
            }
            if (arg.StartsWith("--wmi=", StringComparison.OrdinalIgnoreCase))
            {
                var list = arg.Substring("--wmi=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                wmiFiles.AddRange(list);
                continue;
            }
            if (arg.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("--repo=".Length).Trim().Trim('"');
                if (!string.IsNullOrEmpty(value))
                    repoPath = value;
                continue;
            }
            if (arg.StartsWith("--repo-token=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("--repo-token=".Length).Trim().Trim('"');
                if (!string.IsNullOrEmpty(value))
                    repoToken = value;
                continue;
            }
            positional.Add(arg);
        }

        if (positional.Count > 0)
            outputDir = positional[0];
        if (positional.Count > 1 && int.TryParse(positional[1], out var sec) && sec > 0)
            collectSeconds = sec;

        return (outputDir, collectSeconds, enableEtw, providerFilter, evtxFiles, srumFiles, bitsFiles, wmiFiles, repoPath, repoToken);
    }

    private static bool IncludeProvider(string key, HashSet<string>? filter)
    {
        if (filter == null) return true;
        return filter.Contains(key);
    }

    private static List<IProvider> BuildProviders(HostWitnessSettings settings, bool enableEtw, HashSet<string>? providerFilter, List<string>? evtxFiles = null, List<string>? srumFiles = null, List<string>? bitsFiles = null, List<string>? wmiFiles = null)
    {
        var providers = new List<IProvider>();
        var hasOfflineEvtx = evtxFiles != null && evtxFiles.Count > 0;
        var hasSrum = srumFiles != null && srumFiles.Count > 0;
        var hasBits = bitsFiles != null && bitsFiles.Count > 0;
        var hasWmi = wmiFiles != null && wmiFiles.Count > 0;

        if (IncludeProvider("Process", providerFilter))
            providers.Add(new LiveProcessProvider());
        if (IncludeProvider("Service", providerFilter))
            providers.Add(new LiveServiceProvider());
        if (IncludeProvider("ProcessCrossCheck", providerFilter))
            providers.Add(new ProcessApiCrossCheckProvider());
        if (IncludeProvider("Net", providerFilter))
            providers.Add(new NetConnectionProvider());
        // --evtx implies EventLog: offline .evtx parsing supersedes live-channel reading.
        if (IncludeProvider("EventLog", providerFilter) || hasOfflineEvtx)
        {
            var eventLogProvider = new EventLogProvider();
            if (hasOfflineEvtx)
            {
                foreach (var file in evtxFiles!)
                    eventLogProvider.AddEvtxFile(file);
            }
            providers.Add(eventLogProvider);
        }
        if (IncludeProvider("RecentLnk", providerFilter))
            providers.Add(new RecentLnkProvider());
        if (IncludeProvider("JumpList", providerFilter))
            providers.Add(new JumpListProvider());
        if (IncludeProvider("BrowserHistory", providerFilter))
            providers.Add(new BrowserHistoryProvider());
        if (IncludeProvider("ScheduledTask", providerFilter))
            providers.Add(new ScheduledTaskProvider());
        if (IncludeProvider("PowerShellHistory", providerFilter))
            providers.Add(new PowerShellHistoryProvider());
        if (IncludeProvider("StartupFolder", providerFilter))
            providers.Add(new StartupFolderProvider());
        // SRUM is opt-in (high-volume historical data); --srum supplies the database path(s).
        if (hasSrum)
        {
            var srumProvider = new SrumProvider();
            foreach (var file in srumFiles!)
                srumProvider.AddDatabase(file);
            providers.Add(srumProvider);
        }
        // BITS is opt-in; --bits supplies the qmgr.db path(s).
        if (hasBits)
        {
            var bitsProvider = new BitsProvider();
            foreach (var file in bitsFiles!)
                bitsProvider.AddDatabase(file);
            providers.Add(bitsProvider);
        }
        // WMI persistence is opt-in; --wmi supplies the OBJECTS.DATA path(s).
        if (hasWmi)
        {
            var wmiProvider = new WmiProvider();
            foreach (var file in wmiFiles!)
                wmiProvider.AddRepository(file);
            providers.Add(wmiProvider);
        }

        var allowLiveRegistry = RegistryLivePolicy.IsLiveRegistryEnabled(settings);
        if (!allowLiveRegistry && IncludeProvider("Registry", providerFilter))
            Console.WriteLine("Registry provider skipped: live registry disabled by policy (forensic default).");
        if (allowLiveRegistry && IncludeProvider("Registry", providerFilter))
        {
            var registryProvider = new RegistrySearchProvider();
            registryProvider.AddDefaultQueries();
            providers.Add(registryProvider);
        }

        if (IncludeProvider("OfflineHive", providerFilter))
        {
            var offlineHiveProvider = new OfflineHiveRegistryProvider();
            offlineHiveProvider.AddDefaultHivePaths();
            offlineHiveProvider.SetSnapshotService(new VssSnapshotService());
            providers.Add(offlineHiveProvider);
        }

        // ETW is opt-in via --etw unless an explicit --providers= list includes ETW.
        if (enableEtw || (providerFilter != null && providerFilter.Contains("ETW")))
            providers.Add(new ETWMonitorProvider());

        return providers;
    }
}