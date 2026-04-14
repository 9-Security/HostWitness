using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinDFIR.Providers;

namespace WinDFIR.UI.Views;

public partial class RegistrySearchDialog : Window
{
    public Func<IReadOnlyList<RegistryQuery>, System.Threading.CancellationToken, System.Threading.Tasks.Task>? RunDefaultQueriesAsync { get; set; }
    public Func<RegistryQuery, System.Threading.CancellationToken, System.Threading.Tasks.Task>? RunCustomQueryAsync { get; set; }

    /// <summary>When false, Live Registry default queries are disabled (offline-only mode). Re-run default is hidden/disabled.</summary>
    public bool IsLiveRegistryEnabled { get; set; } = true;

    /// <summary>Editable list of default queries (Name, KeyPath, ValueNamePattern, DataPattern, Recursive). Bound to DataGrid.</summary>
    public List<RegistryQuery> DefaultQueries { get; } = new();

    private readonly List<HiveOption> _hiveOptions = new();
    private System.Threading.CancellationTokenSource? _queryCts;

    public RegistrySearchDialog()
    {
        InitializeComponent();
        DataContext = this;
        LoadDefaultQueries();
        Closing += (_, _) => _queryCts?.Cancel();
        Loaded += (_, _) =>
        {
            if (IsLiveRegistryEnabled && LiveNonForensicNotice != null)
                LiveNonForensicNotice.Visibility = Visibility.Visible;
            if (!IsLiveRegistryEnabled && OfflineOnlyNotice != null && ReRunDefaultButton != null && DefaultQueriesGrid != null)
            {
                OfflineOnlyNotice.Visibility = Visibility.Visible;
                ReRunDefaultButton.Visibility = Visibility.Collapsed;
                DefaultQueriesGrid.Visibility = Visibility.Collapsed;
                if (RunCustomButton != null)
                    RunCustomButton.IsEnabled = false;
                if (CustomKeyPathBox != null)
                    CustomKeyPathBox.IsEnabled = false;
                if (CustomValuePatternBox != null)
                    CustomValuePatternBox.IsEnabled = false;
                if (CustomDataPatternBox != null)
                    CustomDataPatternBox.IsEnabled = false;
                if (CustomRecursiveCheck != null)
                    CustomRecursiveCheck.IsEnabled = false;
                if (CustomHiveCombo != null)
                    CustomHiveCombo.IsEnabled = false;
                if (CustomNameBox != null)
                    CustomNameBox.IsEnabled = false;
            }
        };
        _hiveOptions.Add(new HiveOption("HKEY_CURRENT_USER", RegistryHive.CurrentUser));
        _hiveOptions.Add(new HiveOption("HKEY_LOCAL_MACHINE", RegistryHive.LocalMachine));
        _hiveOptions.Add(new HiveOption("HKEY_USERS", RegistryHive.Users));
        CustomHiveCombo.ItemsSource = _hiveOptions;
        CustomHiveCombo.SelectedIndex = 0;
    }

    private void LoadDefaultQueries()
    {
        DefaultQueries.Clear();
        DefaultQueries.Add(new RegistryQuery { Name = "User Run Key", Hive = RegistryHive.CurrentUser, KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run" });
        DefaultQueries.Add(new RegistryQuery { Name = "System Run Key", Hive = RegistryHive.LocalMachine, KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" });
        DefaultQueries.Add(new RegistryQuery { Name = "Recent Docs MRU", Hive = RegistryHive.CurrentUser, KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", Recursive = true });
        DefaultQueries.Add(new RegistryQuery { Name = "Run Dialog MRU", Hive = RegistryHive.CurrentUser, KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU" });
    }

    private async void ReRunDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunDefaultQueriesAsync == null) return;
        _queryCts?.Cancel();
        _queryCts = new System.Threading.CancellationTokenSource();
        ReRunDefaultButton.IsEnabled = false;
        StatusText.Text = "Running default queries…";
        try
        {
            await RunDefaultQueriesAsync(DefaultQueries, _queryCts.Token);
            StatusText.Text = "Default queries completed. Events appended to Timeline.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Queries cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
        finally
        {
            ReRunDefaultButton.IsEnabled = true;
        }
    }

    private async void RunCustomButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunCustomQueryAsync == null) return;
        if (!IsLiveRegistryEnabled)
        {
            StatusText.Text = "Live registry is disabled by policy. Enable experimental live registry in Settings if needed.";
            return;
        }
        var keyPath = CustomKeyPathBox?.Text?.Trim();
        if (string.IsNullOrEmpty(keyPath))
        {
            StatusText.Text = "Please enter a key path.";
            return;
        }
        var name = CustomNameBox?.Text?.Trim();
        if (string.IsNullOrEmpty(name))
            name = "Custom: " + keyPath;

        var hive = RegistryHive.CurrentUser;
        if (CustomHiveCombo?.SelectedValue is RegistryHive h)
            hive = h;

        var query = new RegistryQuery
        {
            Name = name,
            Hive = hive,
            KeyPath = keyPath,
            ValueNamePattern = string.IsNullOrWhiteSpace(CustomValuePatternBox?.Text) ? null : CustomValuePatternBox.Text.Trim(),
            DataPattern = string.IsNullOrWhiteSpace(CustomDataPatternBox?.Text) ? null : CustomDataPatternBox.Text.Trim(),
            Recursive = CustomRecursiveCheck?.IsChecked == true
        };

        _queryCts?.Cancel();
        _queryCts = new System.Threading.CancellationTokenSource();
        RunCustomButton.IsEnabled = false;
        StatusText.Text = "Running custom query…";
        try
        {
            await RunCustomQueryAsync(query, _queryCts.Token);
            StatusText.Text = "Custom query completed. Events appended to Timeline.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Query cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
        finally
        {
            RunCustomButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class HiveOption
    {
        public string Label { get; }
        public RegistryHive Hive { get; }
        public HiveOption(string label, RegistryHive hive) { Label = label; Hive = hive; }
    }
}
