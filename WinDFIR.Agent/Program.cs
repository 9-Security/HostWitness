using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinDFIR.Core.Index;
using WinDFIR.Core.Normalization;
using WinDFIR.Core.Settings;
using WinDFIR.Core.Snapshot;
using WinDFIR.Providers;

namespace WinDFIR.Agent;

/// <summary>
/// Headless agent: runs the same collectors as the UI app and exports a snapshot to disk.
/// For remote deployment: copy HostWitness.Agent.exe to target, run with output path, copy back the snapshot folder.
/// </summary>
internal static class Program
{
    private const int DefaultCollectSeconds = 30;

    public static async Task<int> Main(string[] args)
    {
        try
        {
        var (outputDir, collectSeconds, enableEtw, providerFilter) = ParseArgs(args);

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

        var providers = BuildProviders(settings, enableEtw, providerFilter);

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

        Console.WriteLine($"Exporting snapshot to {outputDir} (events: {index.EventCount})...");
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
            await exporter.ExportAsync(index, outputDir, exportOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Export failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Done. Snapshot folder: " + Path.GetFullPath(outputDir));
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

    private static (string? outputDir, int collectSeconds, bool enableEtw, HashSet<string>? providerFilter) ParseArgs(string[] args)
    {
        string? outputDir = null;
        var collectSeconds = DefaultCollectSeconds;
        var enableEtw = false;
        HashSet<string>? providerFilter = null;

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
            positional.Add(arg);
        }

        if (positional.Count > 0)
            outputDir = positional[0];
        if (positional.Count > 1 && int.TryParse(positional[1], out var sec) && sec > 0)
            collectSeconds = sec;

        return (outputDir, collectSeconds, enableEtw, providerFilter);
    }

    private static bool IncludeProvider(string key, HashSet<string>? filter)
    {
        if (filter == null) return true;
        return filter.Contains(key);
    }

    private static List<IProvider> BuildProviders(HostWitnessSettings settings, bool enableEtw, HashSet<string>? providerFilter)
    {
        var providers = new List<IProvider>();

        if (IncludeProvider("Process", providerFilter))
            providers.Add(new LiveProcessProvider());
        if (IncludeProvider("Net", providerFilter))
            providers.Add(new NetConnectionProvider());
        if (IncludeProvider("EventLog", providerFilter))
            providers.Add(new EventLogProvider());
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