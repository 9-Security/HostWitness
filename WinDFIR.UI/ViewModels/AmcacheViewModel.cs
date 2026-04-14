using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinDFIR.Core.Snapshot;
using WinDFIR.Providers.Parsers;

namespace WinDFIR.UI.ViewModels;

public class AmcacheItem
{
    public string EntryType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Sha1 { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? LastWriteTimeUtc { get; set; }
    public string InstallDateRaw { get; set; } = string.Empty;
}

public class AmcacheViewModel : BaseViewModel
{
    private readonly string _amcachePath;
    private readonly VssSnapshotService _snapshotService;
    private int _refreshing;
    private bool _refreshPending;

    public AmcacheViewModel()
    {
        _amcachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "AppCompat", "Programs", "Amcache.hve");
        _snapshotService = new VssSnapshotService();
        Entries = new ObservableCollection<AmcacheItem>();
    }

    public ObservableCollection<AmcacheItem> Entries { get; }

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
                var items = await Task.Run(() =>
                {
                    var results = new System.Collections.Generic.List<AmcacheItem>();
                    if (!File.Exists(_amcachePath))
                        return results;

                    VssSnapshotContext? snapshotContext = null;
                    try
                    {
                        snapshotContext = _snapshotService.TryCreateContextForPaths(new[] { _amcachePath }, out _);
                        var resolvedPath = snapshotContext?.ResolvePath(_amcachePath) ?? _amcachePath;

                        var parsed = AmcacheParser.Parse(resolvedPath);
                        foreach (var entry in parsed)
                        {
                            results.Add(new AmcacheItem
                            {
                                EntryType = entry.EntryType,
                                Name = entry.Name,
                                Publisher = entry.Publisher,
                                Version = entry.Version,
                                ProductName = entry.ProductName,
                                Path = entry.Path,
                                Sha1 = entry.Sha1,
                                FileId = entry.FileId,
                                FileSize = entry.FileSize,
                                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                                InstallDateRaw = entry.InstallDateRaw
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"AmcacheViewModel Parse: {ex.Message}");
                    }
                    finally
                    {
                        snapshotContext?.Dispose();
                    }

                    return results;
                });

                try
                {
                    if (Application.Current?.Dispatcher != null)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            Entries.Clear();
                            foreach (var entry in items)
                                Entries.Add(entry);
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
}
