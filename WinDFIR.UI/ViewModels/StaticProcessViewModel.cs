using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace WinDFIR.UI.ViewModels;

public class StaticProcessItem
{
    public string Name { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string CommandLine { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
}

public class StaticProcessViewModel : BaseViewModel
{
    private bool _isLoading;
    private StaticProcessItem? _selectedProcess;
    private bool _hasLoaded;

    public ObservableCollection<StaticProcessItem> Processes { get; } = new();

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public StaticProcessItem? SelectedProcess
    {
        get => _selectedProcess;
        set => SetProperty(ref _selectedProcess, value);
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (IsLoading) return;
        if (_hasLoaded && !force) return;
        IsLoading = true;
        Processes.Clear();

        var succeeded = false;
        try
        {
            await Task.Run(() =>
            {
                LoadProcesses();
            });
            succeeded = true;
        }
        catch
        {
            // Keep previous state if load fails
        }
        finally
        {
            if (succeeded)
            {
                _hasLoaded = true;
            }
            IsLoading = false;
        }
    }

    private void LoadProcesses()
    {
        // 1. Get process list efficiently
        var processList = Process.GetProcesses();

        // 2. Fetch WMI data for Command Line and other details if possible (Map PID -> Info)
        var wmiInfo = new System.Collections.Generic.Dictionary<int, (string CmdLine, string Path)>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process");
            using var collection = searcher.Get();
            foreach (var item in collection)
            {
                var pid = Convert.ToInt32(item["ProcessId"]);
                var cmd = item["CommandLine"]?.ToString() ?? string.Empty;
                var path = item["ExecutablePath"]?.ToString() ?? string.Empty;
                wmiInfo[pid] = (cmd, path);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"StaticProcessViewModel WMI: {ex.Message}"); }

        // 3. Build items
        var items = new System.Collections.Generic.List<StaticProcessItem>();

        foreach (var p in processList)
        {
            try
            {
                var item = new StaticProcessItem
                {
                    Id = p.Id,
                    Name = p.ProcessName,
                    ProcessName = p.ProcessName + ".exe"
                };

                // Type: Application (Window) vs Background
                // Note: MainWindowHandle can be 0 even for apps sometimes, but it's a good proxy
                item.Type = p.MainWindowHandle != IntPtr.Zero ? "Application" : "Background Process";

                // WMI Data
                if (wmiInfo.TryGetValue(p.Id, out var info))
                {
                    item.CommandLine = info.CmdLine;
                    item.Path = info.Path;
                }

                // Publisher (Expensive, might need try-catch per process)
                if (string.IsNullOrEmpty(item.Path))
                {
                    try
                    {
                        item.Path = p.MainModule?.FileName ?? string.Empty;
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"StaticProcessViewModel MainModule: {ex.Message}"); }
                }

                if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                {
                    try
                    {
                        var versionInfo = FileVersionInfo.GetVersionInfo(item.Path);
                        item.Publisher = versionInfo.CompanyName ?? string.Empty;
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"StaticProcessViewModel CompanyName: {ex.Message}"); }
                }

                // Friendly Name (Description)
                // If we want "Name" to be description like Task Manager (e.g. "Google Chrome"), we need FileVersionInfo.FileDescription
                if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                {
                    try
                    {
                        var versionInfo = FileVersionInfo.GetVersionInfo(item.Path);
                        if (!string.IsNullOrEmpty(versionInfo.FileDescription))
                            item.Name = versionInfo.FileDescription;
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"StaticProcessViewModel FileDescription: {ex.Message}"); }
                }

                items.Add(item);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StaticProcessViewModel process {p?.Id}: {ex.Message}");
            }
        }

        // Update UI
        try
        {
            if (Application.Current?.Dispatcher != null)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var item in items.OrderBy(i => i.Name))
                        Processes.Add(item);
                });
        }
        catch (InvalidOperationException) { /* app shutting down */ }
    }
}
