using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WinDFIR.Core.Mft;

namespace WinDFIR.UI.ViewModels;

internal enum MftLoadSourceKind
{
    File,
    Volume,
    Merged
}

public sealed class MftTabViewModel : BaseViewModel
{
    public const int UiEntryCap = 100_000;
    private const int MftParseProgressInterval = 2000;
    private const int RecordSizeDetectionSampleBytes = 8 * 1024 * 1024;

    private readonly record struct MftFilterState(
        string? RecordIndex,
        string? FileName,
        string? Path,
        bool? IsInUse,
        bool TimeStompOnly);

    private string _sourceDisplay;
    private string _status = string.Empty;
    private string _filterRecordIndex = string.Empty;
    private string _filterFileName = string.Empty;
    private string _filterPath = string.Empty;
    private bool? _filterIsInUse;
    private bool? _filterTimeStomp;
    private bool _isLoading;
    private int _pageSize = 500;
    private int _currentPage = 1;
    private string _jumpToPageInput = string.Empty;
    private List<MftEntry> _entries = new();
    private List<MftEntry> _filteredEntries = new();

    internal MftTabViewModel(MftLoadSourceKind sourceKind, string sourceKey, string header, string sourceDisplay)
    {
        SourceKind = sourceKind;
        SourceKey = sourceKey;
        Header = header;
        _sourceDisplay = sourceDisplay;
        PagedEntries = new ObservableCollection<MftEntry>();
        PageSizeOptions = new List<int> { 100, 250, 500, 1000, 2000, 5000 };
    }

    internal MftLoadSourceKind SourceKind { get; }
    public string SourceKey { get; }
    public string Header { get; }

    /// <summary>True for the synthetic "All sources" tab that aggregates entries from the per-source tabs.</summary>
    public bool IsMerged => SourceKind == MftLoadSourceKind.Merged;

    public string SourceDisplay
    {
        get => _sourceDisplay;
        set => SetProperty(ref _sourceDisplay, value ?? string.Empty);
    }

    public IReadOnlyList<MftEntry> Entries => _entries;
    public IReadOnlyList<MftEntry> FilteredEntries => _filteredEntries;
    public ObservableCollection<MftEntry> PagedEntries { get; }
    public List<int> PageSizeOptions { get; }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (value < 1)
                value = 500;
            if (SetProperty(ref _pageSize, value))
                UpdatePagedEntries();
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            int clamped = value;
            int totalPages = TotalPages;
            if (totalPages > 0 && clamped > totalPages)
                clamped = totalPages;
            if (clamped < 1)
                clamped = 1;
            if (SetProperty(ref _currentPage, clamped))
                UpdatePagedEntries();
        }
    }

    public int TotalPages
    {
        get
        {
            int count = _filteredEntries.Count;
            if (count == 0)
                return 1;
            return (int)((count + (long)PageSize - 1) / PageSize);
        }
    }

    public int TotalFilteredCount => _filteredEntries.Count;

    public string JumpToPageInput
    {
        get => _jumpToPageInput;
        set => SetProperty(ref _jumpToPageInput, value ?? string.Empty);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value ?? string.Empty);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string FilterRecordIndex
    {
        get => _filterRecordIndex;
        set
        {
            if (SetProperty(ref _filterRecordIndex, value ?? string.Empty))
                RefreshFilteredEntries();
        }
    }

    public string FilterFileName
    {
        get => _filterFileName;
        set
        {
            if (SetProperty(ref _filterFileName, value ?? string.Empty))
                RefreshFilteredEntries();
        }
    }

    public string FilterPath
    {
        get => _filterPath;
        set
        {
            if (SetProperty(ref _filterPath, value ?? string.Empty))
                RefreshFilteredEntries();
        }
    }

    public bool? FilterIsInUse
    {
        get => _filterIsInUse;
        set
        {
            if (SetProperty(ref _filterIsInUse, value))
                RefreshFilteredEntries();
        }
    }

    public bool? FilterTimeStomp
    {
        get => _filterTimeStomp;
        set
        {
            if (SetProperty(ref _filterTimeStomp, value))
                RefreshFilteredEntries();
        }
    }

    public int FilterIsInUseComboIndex
    {
        get => _filterIsInUse == null ? 0 : (_filterIsInUse == true ? 1 : 2);
        set => FilterIsInUse = value == 0 ? null : value == 1;
    }

    public int FilterTimeStompComboIndex
    {
        get => _filterTimeStomp == true ? 1 : 0;
        set => FilterTimeStomp = value == 1 ? true : null;
    }

    public void BeginLoading(string sourceDisplay, string status)
    {
        SourceDisplay = sourceDisplay;
        ClearLoadedEntries();
        Status = status;
        IsLoading = true;
    }

    public void EndLoading()
    {
        IsLoading = false;
    }

    public void SetFailure(string status)
    {
        ClearLoadedEntries();
        Status = status;
    }

    public void UpdatePagedEntries()
    {
        int total = _filteredEntries.Count;
        int totalPages = total == 0 ? 1 : (int)((total + (long)PageSize - 1) / PageSize);
        if (_currentPage > totalPages)
            _currentPage = totalPages;
        if (_currentPage < 1)
            _currentPage = 1;

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(TotalFilteredCount));
        OnPropertyChanged(nameof(CurrentPage));

        PagedEntries.Clear();
        int start = (_currentPage - 1) * PageSize;
        int end = Math.Min(start + PageSize, total);
        for (int i = start; i < end; i++)
            PagedEntries.Add(_filteredEntries[i]);
    }

    public void GoToPage(int page)
    {
        CurrentPage = page;
    }

    public bool GoToPageFromInput()
    {
        if (string.IsNullOrWhiteSpace(JumpToPageInput))
            return false;
        if (!int.TryParse(JumpToPageInput.Trim(), out int page) || page < 1)
            return false;

        GoToPage(page);
        return true;
    }

    public async Task LoadMftFromBytesAsync(byte[] bytes, string sourceLabel, int recordSize = 1024, char? volumeLetter = null, string? loadNote = null)
    {
        var statusNotes = new List<string>();
        if (recordSize < 64 || recordSize > 65536)
        {
            var detection = MftParser.DetectRecordSize(bytes);
            recordSize = detection.RecordSize;

            string? detectionNote = BuildRecordSizeDetectionNote(detection);
            if (!string.IsNullOrWhiteSpace(detectionNote))
                statusNotes.Add(detectionNote);
        }

        if (!string.IsNullOrWhiteSpace(loadNote))
            statusNotes.Add(loadNote.Trim());

        var parsedEntries = await ParseEntriesForSourceAsync(bytes, recordSize, sourceLabel, volumeLetter).ConfigureAwait(true);
        await CompleteLoadFromParsedEntriesAsync(parsedEntries, sourceLabel, recordSize, statusNotes).ConfigureAwait(true);
    }

    public async Task LoadMftFromStreamAsync(Stream stream, string sourceLabel, int recordSize = 1024, char? volumeLetter = null, string? loadNote = null)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        var statusNotes = new List<string>();
        if (recordSize < 64 || recordSize > 65536)
        {
            var detection = DetectRecordSize(stream);
            recordSize = detection.RecordSize;

            string? detectionNote = BuildRecordSizeDetectionNote(detection);
            if (!string.IsNullOrWhiteSpace(detectionNote))
                statusNotes.Add(detectionNote);
        }

        if (!string.IsNullOrWhiteSpace(loadNote))
            statusNotes.Add(loadNote.Trim());

        var parsedEntries = await ParseEntriesForSourceAsync(stream, recordSize, sourceLabel, volumeLetter).ConfigureAwait(true);
        await CompleteLoadFromParsedEntriesAsync(parsedEntries, sourceLabel, recordSize, statusNotes).ConfigureAwait(true);
    }

    /// <summary>
    /// Populates this tab directly from already-parsed entries (used by the merged "All sources" tab).
    /// The entries are expected to already carry their <c>Source</c> label and resolved <c>FullPath</c> —
    /// no parsing or path re-resolution is performed here (per-source RecordIndex values collide across
    /// volumes, so re-running BuildFullPaths on the combined set would be wrong).
    /// </summary>
    public async Task LoadFromEntriesAsync(IReadOnlyList<MftEntry> entries, string completionStatus)
    {
        var list = entries as List<MftEntry> ?? new List<MftEntry>(entries);
        await ApplyLoadedEntriesAsync(list, completionStatus).ConfigureAwait(true);
    }

    private async Task CompleteLoadFromParsedEntriesAsync(List<MftEntry> parsedEntries, string sourceLabel, int recordSize, List<string> statusNotes)
    {
        if (parsedEntries.Count == 0)
        {
            ClearLoadedEntries();
            Status = AppendStatusNotes(
                "Read the source bytes, but parsed 0 MFT entries. Verify the NTFS source, offsets, encryption state, and record size.",
                statusNotes);
            return;
        }

        var completionStatus = parsedEntries.Count >= UiEntryCap
            ? $"Loaded {UiEntryCap:n0} MFT entries (UI cap {UiEntryCap:n0}; list truncated for display). Source: {sourceLabel} (record size {recordSize} B)."
            : $"Loaded {parsedEntries.Count:n0} MFT entries. Source: {sourceLabel} (record size {recordSize} B).";
        if (statusNotes.Exists(static n =>
                n.Contains("PARTIAL $MFT", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("100 MB read cap", StringComparison.OrdinalIgnoreCase)))
            completionStatus = "PARTIAL / CAPPED MFT SOURCE — " + completionStatus;
        completionStatus = AppendStatusNotes(completionStatus, statusNotes);

        await ApplyLoadedEntriesAsync(parsedEntries, completionStatus).ConfigureAwait(true);
        SourceDisplay = sourceLabel;
    }

    public string? ExportToCsv(string filePath)
    {
        try
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            writer.WriteLine("Source,RecordIndex,FileName,FullPath,CreatedUtc,ModifiedUtc,CreatedUtcFn,ModifiedUtcFn,ParentRecordIndex,IsInUse,TimeStompSuspect,IsDirectory");
            foreach (var entry in _filteredEntries)
            {
                writer.WriteLine(string.Join(",",
                    CsvEscape(entry.Source),
                    entry.RecordIndex,
                    CsvEscape(entry.FileName),
                    CsvEscape(entry.FullPath),
                    entry.CreatedUtc?.ToString("O") ?? string.Empty,
                    entry.ModifiedUtc?.ToString("O") ?? string.Empty,
                    entry.CreatedUtcFn?.ToString("O") ?? string.Empty,
                    entry.ModifiedUtcFn?.ToString("O") ?? string.Empty,
                    entry.ParentRecordIndex?.ToString() ?? string.Empty,
                    entry.IsInUse,
                    entry.TimeStompSuspect,
                    entry.IsDirectory));
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public string? ExportToJson(string filePath)
    {
        try
        {
            var list = new List<object>();
            foreach (var entry in _filteredEntries)
            {
                list.Add(new
                {
                    entry.Source,
                    entry.RecordIndex,
                    entry.FileName,
                    entry.FullPath,
                    CreatedUtc = entry.CreatedUtc?.ToString("O"),
                    ModifiedUtc = entry.ModifiedUtc?.ToString("O"),
                    CreatedUtcFn = entry.CreatedUtcFn?.ToString("O"),
                    ModifiedUtcFn = entry.ModifiedUtcFn?.ToString("O"),
                    entry.ParentRecordIndex,
                    entry.IsInUse,
                    entry.TimeStompSuspect,
                    entry.IsDirectory
                });
            }

            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json, Encoding.UTF8);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<List<MftEntry>> ParseEntriesForSourceAsync(byte[] bytes, int recordSize, string sourceLabel, char? volumeLetter)
    {
        Status = $"{sourceLabel}: loading MFT (parsing in background)...";
        var progress = new Progress<string>(message => Status = $"{sourceLabel}: {message}");
        var parsed = await Task.Run(() => ParseEntries(bytes, recordSize, progress)).ConfigureAwait(true);
        return AddSourceContext(parsed, sourceLabel, volumeLetter);
    }

    private async Task<List<MftEntry>> ParseEntriesForSourceAsync(Stream stream, int recordSize, string sourceLabel, char? volumeLetter)
    {
        Status = $"{sourceLabel}: loading MFT (parsing in background)...";
        var progress = new Progress<string>(message => Status = $"{sourceLabel}: {message}");
        var parsed = await Task.Run(() => ParseEntries(stream, recordSize, progress)).ConfigureAwait(true);
        return AddSourceContext(parsed, sourceLabel, volumeLetter);
    }

    private async Task ApplyLoadedEntriesAsync(List<MftEntry> entries, string completionStatus)
    {
        _entries = entries;
        OnPropertyChanged(nameof(Entries));

        Status = $"Applying filters to {entries.Count:n0} MFT entries...";
        var filterState = CaptureFilterState();
        _filteredEntries = await Task.Run(() => ApplyFilters(entries, filterState)).ConfigureAwait(true);
        OnPropertyChanged(nameof(FilteredEntries));

        _currentPage = 1;
        UpdatePagedEntries();
        Status = completionStatus;
    }

    private void RefreshFilteredEntries()
    {
        _filteredEntries = ApplyFilters(_entries, CaptureFilterState());
        OnPropertyChanged(nameof(FilteredEntries));
        _currentPage = 1;
        UpdatePagedEntries();
    }

    private void ClearLoadedEntries()
    {
        _entries = new List<MftEntry>();
        _filteredEntries = new List<MftEntry>();
        _currentPage = 1;
        PagedEntries.Clear();
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(FilteredEntries));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(TotalFilteredCount));
        OnPropertyChanged(nameof(CurrentPage));
    }

    private MftFilterState CaptureFilterState()
    {
        return new MftFilterState(
            string.IsNullOrWhiteSpace(_filterRecordIndex) ? null : _filterRecordIndex.Trim(),
            string.IsNullOrWhiteSpace(_filterFileName) ? null : _filterFileName.Trim(),
            string.IsNullOrWhiteSpace(_filterPath) ? null : _filterPath.Trim(),
            _filterIsInUse,
            _filterTimeStomp == true);
    }

    private static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "\"\"";
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static List<MftEntry> ApplyFilters(IEnumerable<MftEntry> source, MftFilterState filterState)
    {
        var filtered = source is ICollection<MftEntry> collection
            ? new List<MftEntry>(collection.Count)
            : new List<MftEntry>();

        foreach (var entry in source)
        {
            if (filterState.RecordIndex != null && !entry.RecordIndex.ToString().Contains(filterState.RecordIndex, StringComparison.OrdinalIgnoreCase))
                continue;
            if (filterState.FileName != null && !entry.FileName.Contains(filterState.FileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (filterState.Path != null && !entry.FullPath.Contains(filterState.Path, StringComparison.OrdinalIgnoreCase))
                continue;
            if (filterState.IsInUse.HasValue && entry.IsInUse != filterState.IsInUse.Value)
                continue;
            if (filterState.TimeStompOnly && !entry.TimeStompSuspect)
                continue;

            filtered.Add(entry);
        }

        return filtered;
    }

    private static string? BuildRecordSizeDetectionNote(MftParser.RecordSizeDetectionResult detection)
    {
        if (!detection.IsAutoDetected)
            return "Unable to auto-detect the record size confidently; defaulted to 1024-byte MFT records.";
        if (detection.IsAmbiguous)
            return $"Record size auto-detection was ambiguous; using {detection.RecordSize}-byte MFT records.";
        if (detection.RecordSize != 1024)
            return $"Auto-detected {detection.RecordSize}-byte MFT records for this source.";
        return null;
    }

    private static string AppendStatusNotes(string status, IEnumerable<string> notes)
    {
        var filteredNotes = notes
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => note.Trim())
            .ToList();
        if (filteredNotes.Count == 0)
            return status;

        return status + " " + string.Join(" ", filteredNotes);
    }

    private static MftParser.RecordSizeDetectionResult DetectRecordSize(Stream stream)
    {
        if (!stream.CanRead)
            throw new InvalidOperationException("The MFT input stream must be readable.");
        if (!stream.CanSeek)
            throw new InvalidOperationException("The MFT input stream must support seeking for record-size detection.");

        long originalPosition = stream.Position;
        try
        {
            int sampleLength = RecordSizeDetectionSampleBytes;
            try
            {
                long remaining = Math.Max(0, stream.Length - originalPosition);
                sampleLength = (int)Math.Min(RecordSizeDetectionSampleBytes, remaining);
            }
            catch (IOException)
            {
            }
            catch (NotSupportedException)
            {
            }

            if (sampleLength <= 0)
                return new MftParser.RecordSizeDetectionResult(1024, 0, IsAutoDetected: false, IsAmbiguous: false);

            var sample = new byte[sampleLength];
            int totalRead = 0;
            while (totalRead < sample.Length)
            {
                int read = stream.Read(sample, totalRead, sample.Length - totalRead);
                if (read <= 0)
                    break;
                totalRead += read;
            }

            if (totalRead < sample.Length)
                Array.Resize(ref sample, totalRead);

            return MftParser.DetectRecordSize(sample);
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static List<MftEntry> ParseEntries(byte[] bytes, int recordSize, IProgress<string>? progress)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return ParseEntries(stream, recordSize, progress);
    }

    private static List<MftEntry> ParseEntries(Stream stream, int recordSize, IProgress<string>? progress)
    {
        var parsed = new List<MftEntry>();
        foreach (var entry in MftParser.Parse(stream, recordSize))
        {
            parsed.Add(entry);
            if (parsed.Count % MftParseProgressInterval == 0)
                progress?.Report($"Parsing MFT in background... {parsed.Count:n0} entries found.");
            if (parsed.Count >= UiEntryCap)
                break;
        }

        progress?.Report(parsed.Count == 0
            ? "Building full paths for 0 MFT entries..."
            : $"Building full paths for {parsed.Count:n0} MFT entries...");

        return MftParser.BuildFullPaths(parsed).ToList();
    }

    private static List<MftEntry> AddSourceContext(IEnumerable<MftEntry> entries, string sourceLabel, char? volumeLetter)
    {
        var stampedEntries = new List<MftEntry>();
        foreach (var entry in entries)
        {
            string fullPath = volumeLetter.HasValue
                ? QualifyVolumePath(entry.FullPath, volumeLetter.Value)
                : entry.FullPath;
            stampedEntries.Add(entry with
            {
                Source = sourceLabel,
                FullPath = fullPath
            });
        }

        return stampedEntries;
    }

    private static string QualifyVolumePath(string path, char driveLetter)
    {
        var volumeRoot = $"{char.ToUpperInvariant(driveLetter)}:\\";
        if (string.IsNullOrWhiteSpace(path) || path == ".")
            return volumeRoot;

        var normalizedPath = path.Trim();
        if (normalizedPath.StartsWith(".\\", StringComparison.Ordinal))
            normalizedPath = normalizedPath[2..];
        if (normalizedPath.Length >= 2 && normalizedPath[1] == ':')
            return normalizedPath;
        if (normalizedPath.StartsWith("\\", StringComparison.Ordinal))
            return $"{char.ToUpperInvariant(driveLetter)}:{normalizedPath}";
        return volumeRoot + normalizedPath;
    }
}
