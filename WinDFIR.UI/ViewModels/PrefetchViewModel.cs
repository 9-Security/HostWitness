using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.UI.ViewModels;

public class PrefetchItem
{
    public string Filename { get; set; } = string.Empty;
    public DateTime CreatedTime { get; set; }
    public DateTime ModifiedTime { get; set; }
    public long FileSize { get; set; }
    public string ProcessExe { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public DateTime? LastRunTime { get; set; }
    public PrefetchRecord Record { get; set; } = null!;
}

public class ReferencedFileItem
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
}

public class PrefetchViewModel : BaseViewModel
{
    private readonly string _prefetchFolder;
    private int _refreshing;
    private bool _refreshPending;
    private string _emptyReason = string.Empty;

    public PrefetchViewModel()
    {
        _prefetchFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        PrefetchItems = new ObservableCollection<PrefetchItem>();
        ReferencedFiles = new ObservableCollection<ReferencedFileItem>();
    }

    public ObservableCollection<PrefetchItem> PrefetchItems { get; }
    public ObservableCollection<ReferencedFileItem> ReferencedFiles { get; }

    /// <summary>When list is empty after refresh, explains why (e.g. access denied, folder missing).</summary>
    public string EmptyReason
    {
        get => _emptyReason;
        private set => SetProperty(ref _emptyReason, value ?? string.Empty);
    }

    private PrefetchItem? _selectedItem;
    public PrefetchItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                UpdateReferencedFiles(value);
            }
        }
    }

    public void Refresh()
    {
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _refreshPending = true;
            return;
        }

        try
        {
            do
            {
                _refreshPending = false;
                var currentSelection = _selectedItem;
                var selName = currentSelection?.Filename;

                var (items, emptyReason) = await Task.Run(() =>
                {
                    var results = new System.Collections.Generic.List<PrefetchItem>();
                    string reason = string.Empty;
                    try
                    {
                        if (!Directory.Exists(_prefetchFolder))
                        {
                            reason = $"Prefetch 資料夾不存在: {_prefetchFolder}";
                            return (results, reason);
                        }

                        string[] files;
                        try
                        {
                            files = Directory.GetFiles(_prefetchFolder, "*.pf");
                        }
                        catch (UnauthorizedAccessException)
                        {
                            reason = "讀取 Prefetch 需要管理員權限，請以系統管理員身分執行。";
                            return (results, reason);
                        }
                        catch (IOException ex)
                        {
                            reason = $"無法讀取資料夾: {ex.Message}";
                            return (results, reason);
                        }

                        foreach (var file in files)
                        {
                            PrefetchRecord? record;
                            try
                            {
                                record = PrefetchParser.Parse(file);
                            }
                            catch
                            {
                                continue;
                            }
                            if (record == null)
                                continue;

                            results.Add(new PrefetchItem
                            {
                                Filename = record.PrefetchFileName,
                                CreatedTime = record.CreatedTimeUtc.ToLocalTime(),
                                ModifiedTime = record.ModifiedTimeUtc.ToLocalTime(),
                                FileSize = record.FileSize,
                                ProcessExe = record.ProcessExe,
                                ProcessPath = record.ProcessPath,
                                RunCount = record.RunCount,
                                LastRunTime = record.RunTimesUtc.FirstOrDefault() == default
                                    ? null
                                    : record.RunTimesUtc.FirstOrDefault().ToLocalTime(),
                                Record = record
                            });
                        }

                        if (results.Count == 0)
                        {
                            reason = files.Length == 0
                                ? "資料夾內沒有 .pf 檔案（可能已停用 Prefetch）。"
                                : $"找到 {files.Length} 個 .pf 檔案，但皆無法解析（格式可能不支援）。路徑: {_prefetchFolder}";
                        }
                        return (results, reason);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        reason = "讀取 Prefetch 需要管理員權限，請以系統管理員身分執行。";
                        return (results, reason);
                    }
                    catch (IOException ex)
                    {
                        reason = $"無法讀取: {ex.Message}";
                        return (results, reason);
                    }
                });

                try
                {
                    if (Application.Current?.Dispatcher != null)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            EmptyReason = items.Count == 0 ? emptyReason : string.Empty;
                            PrefetchItems.Clear();
                            PrefetchItem? newSelection = null;
                            foreach (var item in items)
                            {
                                PrefetchItems.Add(item);
                                if (selName != null &&
                                    newSelection == null &&
                                    string.Equals(item.Filename, selName, StringComparison.OrdinalIgnoreCase))
                                    newSelection = item;
                            }
                            if (newSelection != null)
                                SelectedItem = newSelection;
                            else if (_selectedItem == null)
                                ReferencedFiles.Clear();
                        });
                }
                catch (InvalidOperationException) { /* app shutting down */ }
            } while (_refreshPending);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void UpdateReferencedFiles(PrefetchItem? item)
    {
        ReferencedFiles.Clear();
        if (item?.Record == null)
            return;

        foreach (var path in item.Record.ReferencedFiles)
        {
            ReferencedFiles.Add(new ReferencedFileItem
            {
                FileName = Path.GetFileName(path),
                FullPath = path
            });
        }
    }
}
