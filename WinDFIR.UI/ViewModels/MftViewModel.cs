using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinDFIR.Core.IO;
using WinDFIR.Core.Mft;

namespace WinDFIR.UI.ViewModels;

public class MftViewModel : BaseViewModel
{
    private MftTabViewModel? _selectedTab;
    private bool _isLoadingSources;

    public MftViewModel()
    {
        Tabs = new ObservableCollection<MftTabViewModel>();
    }

    public ObservableCollection<MftTabViewModel> Tabs { get; }

    public MftTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
                OnPropertyChanged(nameof(HasSelectedTab));
        }
    }

    public bool HasSelectedTab => SelectedTab != null;

    public async Task ChooseFileAndLoadAsync()
    {
        if (_isLoadingSources)
        {
            if (SelectedTab != null)
                SelectedTab.Status = "MFT load already in progress.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select $MFT or MFT export file",
            Filter = "All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        var filePath = Path.GetFullPath(dialog.FileName);
        var tab = GetOrCreateFileTab(filePath);
        SelectedTab = tab;

        _isLoadingSources = true;
        try
        {
            await LoadFileIntoTabAsync(tab, filePath).ConfigureAwait(true);
        }
        finally
        {
            _isLoadingSources = false;
        }
    }

    public async Task LoadFromVolumesAsync(IEnumerable<char> driveLetters)
    {
        if (_isLoadingSources)
        {
            if (SelectedTab != null)
                SelectedTab.Status = "MFT load already in progress.";
            return;
        }

        var normalizedDriveLetters = NormalizeDriveLetters(driveLetters);
        if (normalizedDriveLetters.Count == 0)
            return;

        var targetTabs = new List<(char DriveLetter, MftTabViewModel Tab)>();
        foreach (var driveLetter in normalizedDriveLetters)
            targetTabs.Add((driveLetter, GetOrCreateVolumeTab(driveLetter)));

        SelectedTab = targetTabs[0].Tab;

        _isLoadingSources = true;
        try
        {
            foreach (var target in targetTabs)
                await LoadVolumeIntoTabAsync(target.Tab, target.DriveLetter).ConfigureAwait(true);
        }
        finally
        {
            _isLoadingSources = false;
        }
    }

    /// <summary>
    /// Builds (or refreshes) the synthetic "All sources" tab by concatenating the entries already loaded in
    /// every per-source tab, so an analyst can search/filter and compare across volumes without switching tabs.
    /// </summary>
    public async Task MergeAllSourcesAsync()
    {
        if (_isLoadingSources)
        {
            if (SelectedTab != null)
                SelectedTab.Status = "MFT load already in progress.";
            return;
        }

        var sourceTabs = Tabs.Where(tab => tab.SourceKind != MftLoadSourceKind.Merged).ToList();
        var mergedTab = GetOrCreateMergedTab();
        SelectedTab = mergedTab;

        if (sourceTabs.Count == 0)
        {
            mergedTab.SetFailure("No MFT sources loaded yet. Load one or more volumes or files first, then merge.");
            return;
        }

        _isLoadingSources = true;
        try
        {
            mergedTab.BeginLoading("All sources (merged)", "Merging loaded MFT sources...");

            var result = BuildMergedEntries(sourceTabs.Select(tab => tab.Entries).ToList(), MftTabViewModel.UiEntryCap);

            var status = $"Merged {result.Entries.Count:n0} MFT entries from {result.SourceCount} source(s). " +
                         "Use the Source column and filters to compare across volumes.";
            if (result.Truncated)
                status = $"Merged view truncated to the {MftTabViewModel.UiEntryCap:n0}-entry display cap (combined sources are larger). " + status;
            if (sourceTabs.Any(tab => tab.Status.Contains("PARTIAL / CAPPED", StringComparison.OrdinalIgnoreCase)))
                status = "PARTIAL / CAPPED MFT SOURCE — " + status;

            await mergedTab.LoadFromEntriesAsync(result.Entries, status).ConfigureAwait(true);
        }
        finally
        {
            mergedTab.EndLoading();
            _isLoadingSources = false;
        }
    }

    /// <summary>Result of concatenating per-source MFT entries for the merged view.</summary>
    public readonly record struct MergedMftResult(IReadOnlyList<MftEntry> Entries, int SourceCount, bool Truncated);

    /// <summary>
    /// Pure concatenation of per-source entry lists for the merged view, honouring the display cap.
    /// Order is source-grouped (each source appended whole); the per-entry <c>Source</c> column disambiguates
    /// rows. <paramref name="cap"/> of 0 means unbounded. Kept static/pure so the merge rules are unit-testable.
    /// </summary>
    public static MergedMftResult BuildMergedEntries(IReadOnlyList<IReadOnlyList<MftEntry>> perSourceEntries, int cap)
    {
        var sources = perSourceEntries.Where(source => source != null).ToList();
        var merged = new List<MftEntry>();
        var truncated = false;

        foreach (var source in sources)
        {
            foreach (var entry in source)
            {
                if (cap > 0 && merged.Count >= cap)
                {
                    truncated = true;
                    break;
                }
                merged.Add(entry);
            }
            if (truncated)
                break;
        }

        return new MergedMftResult(merged, sources.Count, truncated);
    }

    internal MftTabViewModel GetOrCreateMergedTab()
    {
        var existing = Tabs.FirstOrDefault(tab => tab.SourceKind == MftLoadSourceKind.Merged);
        if (existing != null)
            return existing;

        var created = new MftTabViewModel(
            MftLoadSourceKind.Merged,
            "*merged*",
            "All sources",
            "All sources (merged)");
        Tabs.Add(created);
        return created;
    }

    public string? ExportSelectedTabToCsv(string filePath)
    {
        return SelectedTab?.ExportToCsv(filePath) ?? "No MFT table selected.";
    }

    public string? ExportSelectedTabToJson(string filePath)
    {
        return SelectedTab?.ExportToJson(filePath) ?? "No MFT table selected.";
    }

    internal MftTabViewModel GetOrCreateVolumeTab(char driveLetter)
    {
        char normalizedDriveLetter = char.ToUpperInvariant(driveLetter);
        string key = normalizedDriveLetter.ToString();
        var existing = Tabs.FirstOrDefault(tab => tab.SourceKind == MftLoadSourceKind.Volume && string.Equals(tab.SourceKey, key, StringComparison.Ordinal));
        if (existing != null)
            return existing;

        var created = new MftTabViewModel(
            MftLoadSourceKind.Volume,
            key,
            $"{normalizedDriveLetter}:",
            GetVolumeSourceDisplay(normalizedDriveLetter));
        Tabs.Add(created);
        return created;
    }

    private MftTabViewModel GetOrCreateFileTab(string filePath)
    {
        var existing = Tabs.FirstOrDefault(tab => tab.SourceKind == MftLoadSourceKind.File && string.Equals(tab.SourceKey, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;

        string header = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(header))
            header = "MFT file";

        var created = new MftTabViewModel(MftLoadSourceKind.File, filePath, header, filePath);
        Tabs.Add(created);
        return created;
    }

    private async Task LoadFileIntoTabAsync(MftTabViewModel tab, string filePath)
    {
        tab.BeginLoading(filePath, "Reading MFT file...");
        try
        {
            if (!File.Exists(filePath))
            {
                tab.SetFailure("File not found: " + filePath);
                return;
            }

            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            await tab.LoadMftFromStreamAsync(stream, filePath, 0).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            tab.SetFailure("Error: " + ex.Message);
        }
        finally
        {
            tab.EndLoading();
        }
    }

    private async Task LoadVolumeIntoTabAsync(MftTabViewModel tab, char driveLetter)
    {
        char normalizedDriveLetter = char.ToUpperInvariant(driveLetter);
        string defaultSourceDisplay = GetVolumeSourceDisplay(normalizedDriveLetter);
        tab.BeginLoading(defaultSourceDisplay, $"Reading MFT from volume {normalizedDriveLetter}: trying raw volume path...");
        try
        {
            var result = await TryReadMftFromBestAvailableSourceAsync(normalizedDriveLetter, message => tab.Status = message).ConfigureAwait(true);
            if (result.Bytes == null || result.Bytes.Length == 0)
            {
                tab.SetFailure(BuildVolumeFailureMessage(normalizedDriveLetter, result.FailureReason));
                return;
            }

            string sourceLabel = string.IsNullOrWhiteSpace(result.SourceLabel)
                ? defaultSourceDisplay
                : result.SourceLabel;

            await tab.LoadMftFromBytesAsync(result.Bytes, sourceLabel, result.RecordSize, normalizedDriveLetter, result.LoadNote).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            tab.SetFailure($"Failed to load MFT for {normalizedDriveLetter}: {ex.Message}");
        }
        finally
        {
            tab.EndLoading();
        }
    }

    private static async Task<(byte[]? Bytes, int RecordSize, string? SourceLabel, string? FailureReason, string? LoadNote)> TryReadMftFromBestAvailableSourceAsync(char driveLetter, Action<string>? updateStatus)
    {
        var failures = new List<string>();

        updateStatus?.Invoke($"Reading MFT from volume {driveLetter}: trying raw volume path...");
        var rawResult = await Task.Run(() =>
        {
            var readResult = RawDiskReader.ReadMftFromVolumeDetailed(driveLetter);
            var sourceLabel = readResult.UsedPhysicalDriveFallback
                ? $"Drive {driveLetter}: PhysicalDrive partition fallback"
                : $"Drive {driveLetter}: raw volume read";
            return (ReadResult: readResult, SourceLabel: sourceLabel);
        }).ConfigureAwait(true);

        if (rawResult.ReadResult.Bytes is { Length: > 0 })
            return (rawResult.ReadResult.Bytes, rawResult.ReadResult.RecordSize, rawResult.SourceLabel, null, BuildLoadNote(rawResult.ReadResult));

        if (!string.IsNullOrWhiteSpace(rawResult.ReadResult.FailureReason))
            failures.Add($"Raw volume path failed: {rawResult.ReadResult.FailureReason}");

        updateStatus?.Invoke($"Raw volume path failed for {driveLetter}: trying backup privilege direct $MFT read...");
        var backupResult = await Task.Run(() => RawDiskReader.ReadMftFromVolumeViaBackupPrivilegeDetailed(driveLetter)).ConfigureAwait(true);

        if (backupResult.Bytes is { Length: > 0 })
            return (backupResult.Bytes, backupResult.RecordSize, $"Drive {driveLetter}: backup privilege $MFT read", null, BuildLoadNote(backupResult));

        if (!string.IsNullOrWhiteSpace(backupResult.FailureReason))
            failures.Add($"Backup privilege path failed: {backupResult.FailureReason}");

        updateStatus?.Invoke($"Backup privilege path failed for {driveLetter}: trying VSS snapshot...");
        var vssResult = await Task.Run(() => RawDiskReader.ReadMftFromVolumeViaVssDetailed(driveLetter)).ConfigureAwait(true);

        if (vssResult.Bytes is { Length: > 0 })
            return (vssResult.Bytes, vssResult.RecordSize, $"Drive {driveLetter}: VSS snapshot $MFT read", null, BuildLoadNote(vssResult));

        if (!string.IsNullOrWhiteSpace(vssResult.FailureReason))
            failures.Add($"VSS path failed: {vssResult.FailureReason}");

        return (null, 1024, null, string.Join(" | ", failures), null);
    }

    private static string? BuildLoadNote(RawDiskReader.MftReadResult result) =>
        RawDiskReader.GetPartialMftLoadOperatorNote(result);

    private static string GetVolumeSourceDisplay(char driveLetter)
    {
        return $"Volume {char.ToUpperInvariant(driveLetter)}:";
    }

    private static string BuildVolumeFailureMessage(char driveLetter, string? failureReason)
    {
        string guidance = "Run as Administrator, ensure the volume is a local unlocked NTFS volume, or load an exported $MFT file.";
        if (string.IsNullOrWhiteSpace(failureReason))
            return $"Unable to read $MFT from volume {driveLetter}: {guidance}";
        return $"Failed to read $MFT from volume {driveLetter}: {failureReason} {guidance}";
    }

    private static List<char> NormalizeDriveLetters(IEnumerable<char> driveLetters)
    {
        return driveLetters
            .Where(char.IsLetter)
            .Select(char.ToUpperInvariant)
            .Distinct()
            .OrderBy(letter => letter)
            .ToList();
    }
}

