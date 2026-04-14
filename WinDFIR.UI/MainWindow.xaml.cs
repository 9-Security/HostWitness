using System;

using System.Collections.Generic;

using System.Collections.Concurrent;

using System.IO;

using System.Linq;

using System.Text;

using System.Threading;

using System.Threading.Tasks;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Controls.Primitives;

using System.Windows.Input;

using System.Windows.Media;

using System.Windows.Threading;

using Microsoft.Win32;

using WinDFIR.Core;

using WinDFIR.Core.Entities;

using WinDFIR.Core.Index;

using WinDFIR.Core.Normalization;

using WinDFIR.Core.Snapshot;

using WinDFIR.Core.Settings;

using WinDFIR.Providers;

using WinDFIR.UI.Services;

using WinDFIR.UI.ViewModels;

using WinDFIR.UI.Views;



namespace WinDFIR.UI;



public partial class MainWindow : Window

{

    private const int MaxUiEvents = 200_000;

    /// <summary>Cap per dispatcher flush to keep the UI thread responsive under bursts; queue is drained across multiple flushes.</summary>
    private const int MaxUiEventsPerFlushIteration = 2000;

    /// <summary>When true, <see cref="FlushUiEvents"/> schedules another dispatcher callback so pending items are not stranded.</summary>
    internal static bool ShouldScheduleAnotherUiFlush(bool pendingQueueIsNonEmpty) => pendingQueueIsNonEmpty;

    private readonly IActivityIndex _index;

    private readonly List<IProvider> _providers;

    private readonly ISnapshotExporter _snapshotExporter;

    private bool _isCollecting = false;

    /// <summary>When true, Timeline / Live Process / Live TCP skip automatic refresh (Live Stream has its own pause flag).</summary>
    private bool _isDynamicPaused = false;

    private bool _isLiveStreamPaused = true;

    private DispatcherTimer? _dynamicViewsRefreshTimer;

    private bool _isLiveTcpResolveEnabled = false;

    private bool _isTabSwitching = false;

    private bool _isSavingSessionOnClose = false;

    private bool _allowCloseAfterSessionSave = false;

    private DateTime _lastRefreshUtc = DateTime.MinValue;

    private DateTime _lastDynamicViewRefreshUtc = DateTime.MinValue;

    private DateTime _lastBrowserRefreshUtc = DateTime.MinValue;

    private DateTime _lastCacheStatusUtc = DateTime.MinValue;

    private DateTime _lastEtwThrottleStatusUtc = DateTime.MinValue;

    private DateTime _lastUiQueueStatusUtc = DateTime.MinValue;

    private readonly ConcurrentQueue<ActivityEvent> _pendingUiEvents = new();

    private int _uiFlushScheduled = 0;

    private long _uiEventCount = 0;

    private long _uiDroppedEvents = 0;

    private long _lastUiDroppedReported = 0;

    private long _lastEvictedReported = 0;

    private DateTime _lastEvictedReportUtc = DateTime.MinValue;

    private DateTime _lastIndexTrimUtc = DateTime.MinValue;

    private readonly ViewRegistryService _viewRegistry = new ViewRegistryService();

    private readonly MainViewModel _mainViewModel;

    private readonly RegistrySearchProvider? _registryProvider;

    private readonly HostWitnessSettings _runtimeSettings;
    private bool _showStatusBarDiagnostics = true;



    public MainWindow()

    {

        InitializeComponent();



        HostWitnessSettings.EnsureSettingsFile();

        _runtimeSettings = HostWitnessSettings.Load();

        FontSize = HostWitnessSettings.GetEffectiveFontSize(_runtimeSettings);
        _showStatusBarDiagnostics = _runtimeSettings.Ui.ShowStatusBarDiagnostics;
        CacheStatusTextBlock.Visibility = _showStatusBarDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        EtwThrottleTextBlock.Visibility = _showStatusBarDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        UiBackpressureTextBlock.Visibility = _showStatusBarDiagnostics ? Visibility.Visible : Visibility.Collapsed;



        var maxEvents = _runtimeSettings.Index?.MaxEvents ?? 200_000;

        _index = new InMemoryActivityIndex(maxEvents == 0 ? 0 : maxEvents);

        _snapshotExporter = new SnapshotExporter();

        

        // Initialize providers (StubProvider only in Debug to avoid mock events in Release)

        _providers = new List<IProvider>

        {

#if DEBUG

            new StubProvider(),

#endif

            new LiveProcessProvider(),

            new NetConnectionProvider(),

            new ETWMonitorProvider(),

            new EventLogProvider(),

            new RecentLnkProvider(),

            new JumpListProvider(),

            new BrowserHistoryProvider()

        };



        var allowLiveRegistry = RegistryLivePolicy.IsLiveRegistryEnabled(_runtimeSettings);

        _registryProvider = allowLiveRegistry ? new RegistrySearchProvider() : null;

        if (_registryProvider != null)

        {

            _registryProvider.AddDefaultQueries();

            _providers.Add(_registryProvider);

        }



        var offlineHiveProvider = new OfflineHiveRegistryProvider();

        offlineHiveProvider.AddDefaultHivePaths();

        if (_runtimeSettings.Ui?.RawHiveSources != null)

        {

            foreach (var raw in _runtimeSettings.Ui.RawHiveSources)

            {

                if (raw.SizeBytes > 0)

                    offlineHiveProvider.AddRawHive(raw.DriveNumber, raw.OffsetBytes, raw.SizeBytes, raw.HiveName ?? "SYSTEM");

            }

        }

        offlineHiveProvider.SetSnapshotService(new VssSnapshotService());

        _providers.Add(offlineHiveProvider);



        foreach (var provider in _providers)

        {

            provider.EventProduced += OnEventProduced;

        }

        

        // Initialize views

        var timelineView = new TimelineView(_index);

        var processView = new ProcessView(_index);

        var staticProcessView = new StaticProcessView();

        var systemInfoView = new SystemInfoView();

        var recentFilesView = new RecentFilesView(_index);

        var prefetchView = new PrefetchView();

        var amcacheView = new AmcacheView();

        var autorunView = new AutorunView(_index);

        var eventLogView = new EventLogView(_index);

        var networkView = new NetworkView(_index);

        var liveStreamView = new LiveStreamView(_index);

        var browsingHistoryView = new BrowsingHistoryView(_index);

        var liveTcpView = new LiveTcpView(_index);

        var mftView = new MftView();

        

        // Register views with registry (decouples view/detach state for future Docking; TECH_DEBT sections 2 and 4)

        _viewRegistry.RegisterDynamicView("Timeline", timelineView);

        _viewRegistry.RegisterDynamicView("Process", processView);

        _viewRegistry.RegisterDynamicView("LiveStream", liveStreamView);

        _viewRegistry.RegisterDynamicView("LiveTcp", liveTcpView);

        _viewRegistry.RegisterStaticView("SystemInfo", systemInfoView);

        _viewRegistry.RegisterStaticView("StaticProcess", staticProcessView);

        _viewRegistry.RegisterStaticView("RecentFiles", recentFilesView);

        _viewRegistry.RegisterStaticView("Prefetch", prefetchView);

        _viewRegistry.RegisterStaticView("Amcache", amcacheView);

        _viewRegistry.RegisterStaticView("Autorun", autorunView);

        _viewRegistry.RegisterStaticView("EventLog", eventLogView);

        _viewRegistry.RegisterStaticView("Network", networkView);

        _viewRegistry.RegisterStaticView("BrowsingHistory", browsingHistoryView);

        _viewRegistry.RegisterStaticView("MFT", mftView);

        

        // Ensure Live Stream is paused by default

        liveStreamView.Pause();

        _isLiveStreamPaused = true;

        // Live Stream stays paused until the user clicks Play on that tab; other dynamic views refresh while collecting.

        UpdatePlayPauseButtonIcon(TimelineStartButton, false);

        UpdatePlayPauseButtonIcon(ProcessStartButton, false);

        UpdatePlayPauseButtonIcon(LiveStreamStartButton, true);

        UpdatePlayPauseButtonIcon(LiveTcpStartButton, false);



        // Tab state in ViewModel for decoupling; bindings sync TabControl.SelectedIndex

        _mainViewModel = new MainViewModel

        {

            SelectedStaticTabIndex = 0,

            SelectedDynamicTabIndex = -1

        };

        DataContext = _mainViewModel;



        // When Registry is offline-only or Live: hint mode in menu (forensic vs non-forensic)

        if (RegistrySearchMenuItem != null)

        {

            if (_registryProvider == null)

            {

                RegistrySearchMenuItem.Header = "Registry _Search... (offline: custom only)";

                RegistrySearchMenuItem.ToolTip = "Live Registry disabled by policy (forensic default). Enable it in Settings only for non-forensic triage.";

            }

            else

            {

                RegistrySearchMenuItem.ToolTip = "Re-run default registry queries or run a custom query. Live mode (non-forensic). For forensic use prefer Offline Hive (Settings: Registry use offline only).";

            }

        }



        // Drill-down: from Live Process selection, switch to Timeline and filter by process

        processView.DrillDownRequested = processKey =>

        {

            _mainViewModel.SelectedDynamicTabIndex = 0; // Timeline

            UpdateSharedContent();

            TimelineViewControl?.ApplyProcessFilter(processKey);

        };



        _isTabSwitching = true;

        _isTabSwitching = false;



        // Set initial content

        UpdateSharedContent();

        UpdateDrillDownMenuItemState();



        // Providers are started after optional session restore in MainWindow_Loaded.

    }



    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)

    {

        // Apply saved layout if present (position, size, detached tabs). Startup tab is always System Info.

        var layoutState = LayoutPersistence.LoadState();

        if (layoutState != null)

        {

            var left = layoutState.Left;

            var top = layoutState.Top;

            var width = layoutState.Width;

            var height = layoutState.Height;

            if (!double.IsNaN(width) && width > 200) Width = width;

            if (!double.IsNaN(height) && height > 200) Height = height;

            if (!double.IsNaN(left) && left >= -100) Left = left;

            if (!double.IsNaN(top) && top >= -100) Top = top;



            foreach (var detached in layoutState.DetachedDynamicTabs)

                DetachDynamicTab(detached.Key, detached);

            foreach (var detached in layoutState.DetachedStaticTabs)

                DetachStaticTab(detached.Key, detached);

        }



        // Default startup tab: System Info (static index 0), no dynamic tab selected

        _mainViewModel.SelectedStaticTabIndex = 0;

        _mainViewModel.SelectedDynamicTabIndex = -1;

        if (StaticTabControl != null) StaticTabControl.SelectedIndex = 0;

        if (DynamicTabControl != null) DynamicTabControl.SelectedIndex = -1;

        UpdateSharedContent();

        try

        {

            // Restore session before starting live providers to avoid mixing restored + new live events.

            TryRestoreLastSession();

            await StartProvidersAsync();

            SetupDynamicViewsRefreshTimer();

        }

        catch (Exception ex)

        {

            MessageBox.Show($"Startup error: {ex.Message}", "HostWitness", MessageBoxButton.OK, MessageBoxImage.Warning);

        }

    }

    /// <summary>
    /// Refreshes Timeline / Live Process / Live TCP on a timer so views stay current when few index events arrive
    /// (FlushUiEvents only runs when the UI event queue is flushed).
    /// </summary>
    private void SetupDynamicViewsRefreshTimer()
    {
        if (_dynamicViewsRefreshTimer != null)
            return;

        _dynamicViewsRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _dynamicViewsRefreshTimer.Tick += (_, _) => OnDynamicViewsRefreshTimerTick();
        _dynamicViewsRefreshTimer.Start();
    }

    private void OnDynamicViewsRefreshTimerTick()
    {
        if (!_isCollecting || _isDynamicPaused)
            return;

        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastDynamicViewRefreshUtc).TotalMilliseconds < 1000)
            return;

        _lastDynamicViewRefreshUtc = nowUtc;

        if (DynamicTabControl?.SelectedItem == TimelineTab && TimelineViewControl != null)
            TimelineViewControl.Refresh();
        else if (DynamicTabControl?.SelectedItem == ProcessTab && ProcessViewControl != null)
            ProcessViewControl.Refresh();
        else if (DynamicTabControl?.SelectedItem == LiveTcpTab && LiveTcpViewControl != null)
            LiveTcpViewControl.Refresh();
    }



    private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)

    {

        _dynamicViewsRefreshTimer?.Stop();

        // Save layout (position, size, tab selection)

        try

        {

            var dynIdx = DynamicTabControl?.SelectedIndex ?? -1;

            var staticIdx = StaticTabControl?.SelectedIndex ?? 0;

            LayoutPersistence.Save(

                Left,

                Top,

                Width,

                Height,

                dynIdx,

                staticIdx,

                GetDetachedDynamicLayouts(),

                GetDetachedStaticLayouts());

        }

        catch

        {

            // ignore

        }



        if (_allowCloseAfterSessionSave)

            return;

        if (_isSavingSessionOnClose)

        {

            e.Cancel = true;

            return;

        }



        var current = _snapshotIndex ?? _index;



        e.Cancel = true;

        _isSavingSessionOnClose = true;

        try

        {

            var stopExceptions = await ProviderLifecycleHelper.StopProvidersAsync(_providers);
            LogProviderLifecycleExceptions("Provider stop on exit failed", stopExceptions);

            if (stopExceptions.Count > 0)
            {
                MessageBox.Show(
                    "One or more providers failed to stop cleanly during exit. See the application log for details.",
                    "Provider Shutdown",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var folder = SessionPersistence.GetDefaultSessionFolder();
            if (current.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).Any())
                await SessionPersistence.SaveAsync(current, folder);
            else
                SessionPersistence.ClearSavedSession(folder);

        }

        catch (Exception ex)

        {

            LogToAppLog("Session save on exit failed", ex);

            System.Diagnostics.Debug.WriteLine($"Session save on exit failed: {ex.Message}");

            MessageBox.Show($"Session save on exit failed.\n\n{ex.Message}", "Session Save", MessageBoxButton.OK, MessageBoxImage.Warning);

        }

        finally

        {

            _isSavingSessionOnClose = false;

            _allowCloseAfterSessionSave = true;

            Close();

        }

    }

    private void LogProviderLifecycleExceptions(string context, IReadOnlyCollection<Exception> exceptions)
    {
        foreach (var exception in exceptions)
        {
            LogToAppLog(context, exception);
            System.Diagnostics.Debug.WriteLine($"{context}: {exception.Message}");
        }
    }




    private void TryRestoreLastSession()

    {

        var folder = SessionPersistence.GetDefaultSessionFolder();

        var sessionInfo = SessionPersistence.GetSavedSessionInfo(folder);

        if (sessionInfo.EventCount <= 0) return;

        if (sessionInfo.SessionSchemaVersion > SessionPersistence.CurrentSessionSchemaVersion)

        {

            MessageBox.Show(

                "A saved session was found, but it was written by a newer version of HostWitness. Update HostWitness to restore this session.",

                "Restore session",

                MessageBoxButton.OK,

                MessageBoxImage.Information);

            return;

        }

        var savedAtLabel = string.IsNullOrEmpty(sessionInfo.SavedAt) ? "" : $" (saved {ParseSavedAtLabel(sessionInfo.SavedAt)})";

        var retentionWarning = string.Empty;

        if (_index is InMemoryActivityIndex memoryIndex &&

            memoryIndex.MaxEventCapacity > 0 &&

            sessionInfo.EventCount > memoryIndex.MaxEventCapacity)

        {

            retentionWarning = $"\n\nCurrent live index cap is {memoryIndex.MaxEventCapacity:N0} events, so restore will keep the newest {memoryIndex.MaxEventCapacity:N0}.";

        }



        var result = MessageBox.Show(

            $"Restore previous session? ({sessionInfo.EventCount:N0} events{savedAtLabel}){retentionWarning}",

            "Restore session",

            MessageBoxButton.YesNo,

            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try

        {

            var loadResult = SessionPersistence.TryLoadSession(folder);

            if (!loadResult.Success)

            {

                var detail = loadResult.Failure switch

                {

                    SessionLoadFailureKind.UnsupportedSchemaVersion =>

                        "This session format is not supported by this build.",

                    SessionLoadFailureKind.MissingOrEmptyTimeline =>

                        "Session metadata exists but the timeline file is missing or empty.",

                    SessionLoadFailureKind.TimelineParseError =>

                        "The timeline file could not be read.",

                    _ => "Unknown error."

                };

                MessageBox.Show(detail, "Restore session", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;

            }

            var events = loadResult.Events;

            foreach (var evt in events)

                _index.AddEvent(evt);



            TimelineViewControl?.Refresh();



            if (_index is InMemoryActivityIndex currentIndex &&

                currentIndex.MaxEventCapacity > 0 &&

                events.Count > currentIndex.MaxEventCapacity)

            {

                MessageBox.Show(

                    $"Loaded {events.Count:N0} events from session. The current live index retained the newest {currentIndex.MaxEventCapacity:N0} events.",

                    "Session restored",

                    MessageBoxButton.OK,

                    MessageBoxImage.Information);

                return;

            }



            MessageBox.Show($"Restored {events.Count:N0} events to the timeline.", "Session restored", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            LogToAppLog("Session restore failed", ex);

            MessageBox.Show($"Failed to restore session: {ex.Message}", "Restore session", MessageBoxButton.OK, MessageBoxImage.Warning);

        }

    }



    /// <summary>Appends a single line to %AppData%\HostWitness\logs\app.log for runtime errors (Session/Layout/SQLite etc.).</summary>

    private static void LogToAppLog(string context, Exception ex)

    {

        try

        {

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HostWitness", "logs");

            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "app.log");

            var line = $"[{DateTime.UtcNow:O}] {context}: {ex.Message}{Environment.NewLine}";

            File.AppendAllText(path, line);

        }

        catch

        {

            // ignore logging failures

        }

    }



    private static string ParseSavedAtLabel(string savedAtUtc)

    {

        if (string.IsNullOrEmpty(savedAtUtc)) return "";

        if (DateTime.TryParse(savedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc))

            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        return savedAtUtc;

    }



    private void SaveSessionMenuItem_Click(object sender, RoutedEventArgs e)

    {

        var current = _snapshotIndex ?? _index;

        try

        {

            var folder = SessionPersistence.GetDefaultSessionFolder();

            var count = SessionPersistence.Save(current, folder);

            MessageBox.Show($"Session saved ({count} events). You can restore it on next start.", "Save session", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            LogToAppLog("Save session (menu) failed", ex);

            MessageBox.Show($"Failed to save session: {ex.Message}", "Save session", MessageBoxButton.OK, MessageBoxImage.Warning);

        }

    }



    private void SaveLayoutMenuItem_Click(object sender, RoutedEventArgs e)

    {

        try

        {

            var dynIdx = DynamicTabControl?.SelectedIndex ?? -1;

            var staticIdx = StaticTabControl?.SelectedIndex ?? 0;

            LayoutPersistence.Save(

                Left,

                Top,

                Width,

                Height,

                dynIdx,

                staticIdx,

                GetDetachedDynamicLayouts(),

                GetDetachedStaticLayouts());

            MessageBox.Show("Layout saved. It will be restored on next start.", "Save layout", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            LogToAppLog("Save layout failed", ex);

            MessageBox.Show($"Failed to save layout: {ex.Message}", "Save layout", MessageBoxButton.OK, MessageBoxImage.Warning);

        }

    }



    private void RestoreLayoutMenuItem_Click(object sender, RoutedEventArgs e)

    {

        try

        {

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HostWitness", "layout.json");

            if (File.Exists(path)) File.Delete(path);

            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _mainViewModel.SelectedStaticTabIndex = 0;

            _mainViewModel.SelectedDynamicTabIndex = -1;

            if (StaticTabControl != null) StaticTabControl.SelectedIndex = 0;

            if (DynamicTabControl != null) DynamicTabControl.SelectedIndex = -1;

            UpdateSharedContent();

            MessageBox.Show("Layout reset to default. Position/size will be centered on next start.", "Restore layout", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            LogToAppLog("Restore layout failed", ex);

            MessageBox.Show($"Failed to restore layout: {ex.Message}", "Restore layout", MessageBoxButton.OK, MessageBoxImage.Warning);

        }

    }



    private void MainWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)

    {

        var mod = e.KeyboardDevice.Modifiers;

        var ctrl = (mod & System.Windows.Input.ModifierKeys.Control) != 0;

        var shift = (mod & System.Windows.Input.ModifierKeys.Shift) != 0;

        if (ctrl && e.Key == System.Windows.Input.Key.O)

        {

            OpenSnapshotMenuItem_Click(sender, e);

            e.Handled = true;

        }

        else if (ctrl && e.Key == System.Windows.Input.Key.W)

        {

            CloseSnapshotMenuItem_Click(sender, e);

            e.Handled = true;

        }

        else if (ctrl && e.Key == System.Windows.Input.Key.E)

        {

            ExportButton_Click(sender, e);

            e.Handled = true;

        }

        else if (ctrl && e.Key == System.Windows.Input.Key.L)

        {

            if (TimelineViewControl != null && _mainViewModel.CurrentContentKey == "Timeline")

            {

                TimelineViewControl.Clear();

                e.Handled = true;

            }

        }

        else if (ctrl && shift && e.Key == System.Windows.Input.Key.D)

        {

            DrillDownButton_Click(sender, e);

            e.Handled = true;

        }

    }



    private TimelineView? TimelineViewControl => _viewRegistry.GetDynamicView("Timeline") as TimelineView;

    private ProcessView? ProcessViewControl => _viewRegistry.GetDynamicView("Process") as ProcessView;

    private StaticProcessView? StaticProcessViewControl => _viewRegistry.GetStaticView("StaticProcess") as StaticProcessView;

    private SystemInfoView? SystemInfoViewControl => _viewRegistry.GetStaticView("SystemInfo") as SystemInfoView;

    private RecentFilesView? RecentFilesViewControl => _viewRegistry.GetStaticView("RecentFiles") as RecentFilesView;

    private PrefetchView? PrefetchViewControl => _viewRegistry.GetStaticView("Prefetch") as PrefetchView;

    private AmcacheView? AmcacheViewControl => _viewRegistry.GetStaticView("Amcache") as AmcacheView;

    private AutorunView? AutorunViewControl => _viewRegistry.GetStaticView("Autorun") as AutorunView;

    private EventLogView? EventLogViewControl => _viewRegistry.GetStaticView("EventLog") as EventLogView;

    private NetworkView? NetworkViewControl => _viewRegistry.GetStaticView("Network") as NetworkView;

    private LiveStreamView? LiveStreamViewControl => _viewRegistry.GetDynamicView("LiveStream") as LiveStreamView;

    private BrowsingHistoryView? BrowsingHistoryViewControl => _viewRegistry.GetStaticView("BrowsingHistory") as BrowsingHistoryView;

    private LiveTcpView? LiveTcpViewControl => _viewRegistry.GetDynamicView("LiveTcp") as LiveTcpView;

    

    private void UpdateSharedContent()

    {

        if (SharedContentArea == null) return;



        var key = _mainViewModel.CurrentContentKey;

        if (string.IsNullOrEmpty(key))

            return;



        var isDynamic = MainViewModel.DynamicTabKeys.Contains(key);

        var isDetached = isDynamic ? IsDynamicTabDetached(key) : IsStaticTabDetached(key);

        var title = isDynamic ? GetDynamicTabTitle(key) : GetStaticTabTitle(key);

        var view = isDynamic ? GetDynamicViewByKey(key) : GetStaticViewByKey(key);

        SharedContentArea.Content = isDetached ? CreateDetachedPlaceholder(title) : view;

        UpdateDetachButtonState();

    }



    private void UpdateDetachButtonState()

    {

        var dynKey = GetSelectedDynamicTabKey();

        var staticKey = GetSelectedStaticTabKey();

        var key = dynKey ?? staticKey;

        var isRestore = key != null && (dynKey != null ? IsDynamicTabDetached(key) : IsStaticTabDetached(key));

        _mainViewModel.UpdateDetachState(isRestore);

    }



    private string? GetSelectedDynamicTabKey()

    {

        if (DynamicTabControl?.SelectedItem == TimelineTab) return "Timeline";

        if (DynamicTabControl?.SelectedItem == LiveStreamTab) return "LiveStream";

        if (DynamicTabControl?.SelectedItem == ProcessTab) return "Process";

        if (DynamicTabControl?.SelectedItem == LiveTcpTab) return "LiveTcp";

        return null;

    }



    private string GetDynamicTabTitle(string key)

    {

        return key switch

        {

            "Timeline" => "Timeline View",

            "LiveStream" => "Live Stream",

            "Process" => "Live Process",

            "LiveTcp" => "Live TCP View",

            _ => key

        };

    }



    private bool IsDynamicTabDetached(string key) => _viewRegistry.IsDynamicDetached(key);



    private UserControl? GetDynamicViewByKey(string key) => _viewRegistry.GetDynamicView(key);



    private FrameworkElement CreateDetachedPlaceholder(string title)

    {

        return new Border

        {

            BorderBrush = Brushes.LightGray,

            BorderThickness = new Thickness(1),

            Padding = new Thickness(16),

            Child = new TextBlock

            {

                Text = $"{title} is detached. Use Restore to bring it back.",

                Foreground = Brushes.DimGray

            }

        };

    }



    private void DynamicDetachButton_Click(object sender, RoutedEventArgs e)

    {

        var dynKey = GetSelectedDynamicTabKey();

        var staticKey = GetSelectedStaticTabKey();

        if (dynKey != null)

        {

            if (IsDynamicTabDetached(dynKey))

                RestoreDetachedTab(dynKey);

            else

                DetachDynamicTab(dynKey);

            return;

        }

        if (staticKey != null)

        {

            if (IsStaticTabDetached(staticKey))

                RestoreStaticTab(staticKey);

            else

                DetachStaticTab(staticKey);

        }

    }



    private void DynamicTabContextMenu_Opened(object sender, RoutedEventArgs e)

    {

        if (sender is not System.Windows.Controls.ContextMenu cm || cm.PlacementTarget is not System.Windows.Controls.TabItem tab)

            return;

        var key = tab.Tag as string;

        if (string.IsNullOrEmpty(key))

            return;

        var detached = IsDynamicTabDetached(key);

        if (cm.Items.Count >= 2)

        {

            if (cm.Items[0] is System.Windows.Controls.MenuItem detachItem)

                detachItem.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;

            if (cm.Items[1] is System.Windows.Controls.MenuItem restoreItem)

                restoreItem.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;

        }

    }



    private void DynamicTabDetachMenuItem_Click(object sender, RoutedEventArgs e)

    {

        if (sender is not System.Windows.Controls.MenuItem mi || mi.Parent is not System.Windows.Controls.ContextMenu cm || cm.PlacementTarget is not System.Windows.Controls.TabItem tab)

            return;

        var key = tab.Tag as string;

        if (!string.IsNullOrEmpty(key) && !IsDynamicTabDetached(key))

            DetachDynamicTab(key);

    }



    private void DynamicTabRestoreMenuItem_Click(object sender, RoutedEventArgs e)

    {

        if (sender is not System.Windows.Controls.MenuItem mi || mi.Parent is not System.Windows.Controls.ContextMenu cm || cm.PlacementTarget is not System.Windows.Controls.TabItem tab)

            return;

        var key = tab.Tag as string;

        if (!string.IsNullOrEmpty(key) && IsDynamicTabDetached(key))

            RestoreDetachedTab(key);

    }



    private void DetachDynamicTab(string key, LayoutPersistence.DetachedTabLayout? detachedLayout = null)

    {

        if (_viewRegistry.TryGetDetachedDynamic(key, out var existingWindow, out _))

        {

            existingWindow!.Activate();

            return;

        }



        var view = GetDynamicViewByKey(key);

        if (view == null)

            return;



        var window = new DetachedTabWindow(GetDynamicTabTitle(key), view)

        {

            Owner = this

        };

        ApplyDetachedLayout(window, detachedLayout);



        window.SetToolBar(CreateDetachedToolBar(key));

        window.RestoreRequested += (_, restoredView) =>

        {

            RestoreDetachedTab(key, restoredView, closeWindow: false);

        };



        _viewRegistry.SetDetachedDynamic(key, window, view);

        window.Show();



        if (DynamicTabControl?.SelectedItem == GetDynamicTabByKey(key))

        {

            SharedContentArea.Content = CreateDetachedPlaceholder(GetDynamicTabTitle(key));

        }



        UpdateDetachButtonState();

    }



    private void RestoreDetachedTab(string key, UserControl? restoredView = null, bool closeWindow = true)

    {

        if (!_viewRegistry.TryGetDetachedDynamic(key, out var window, out var view))

            return;



        if (closeWindow && window!.IsVisible)

        {

            window.SuppressRestore = true;

            window.Close();

        }



        var viewToShow = restoredView ?? view;

        if (viewToShow == null)

            return;



        _viewRegistry.RestoreDynamic(key);



        if (DynamicTabControl?.SelectedItem == GetDynamicTabByKey(key))

        {

            SharedContentArea.Content = viewToShow;

        }



        UpdateDetachButtonState();

    }



    private TabItem? GetDynamicTabByKey(string key)

    {

        return key switch

        {

            "Timeline" => TimelineTab,

            "LiveStream" => LiveStreamTab,

            "Process" => ProcessTab,

            "LiveTcp" => LiveTcpTab,

            _ => null

        };

    }



    private string? GetSelectedStaticTabKey()

    {

        if (StaticTabControl?.SelectedItem == SystemInfoTab) return "SystemInfo";

        if (StaticTabControl?.SelectedItem == StaticProcessTab) return "StaticProcess";

        if (StaticTabControl?.SelectedItem == RecentFilesTab) return "RecentFiles";

        if (StaticTabControl?.SelectedItem == PrefetchTab) return "Prefetch";

        if (StaticTabControl?.SelectedItem == AmcacheTab) return "Amcache";

        if (StaticTabControl?.SelectedItem == AutorunTab) return "Autorun";

        if (StaticTabControl?.SelectedItem == EventLogTab) return "EventLog";

        if (StaticTabControl?.SelectedItem == NetworkTab) return "Network";

        if (StaticTabControl?.SelectedItem == BrowsingHistoryTab) return "BrowsingHistory";

        if (StaticTabControl?.SelectedItem == MftTab) return "MFT";

        return null;

    }



    private string GetStaticTabTitle(string key)

    {

        return key switch

        {

            "SystemInfo" => "System Info",

            "StaticProcess" => "Process View",

            "RecentFiles" => "Recent Files",

            "Prefetch" => "Prefetch",

            "Amcache" => "Amcache",

            "Autorun" => "Autorun",

            "EventLog" => "Event Log",

            "Network" => "Netstat",

            "BrowsingHistory" => "Browsing History",

            "MFT" => "MFT",

            _ => key

        };

    }



    private bool IsStaticTabDetached(string key) => _viewRegistry.IsStaticDetached(key);



    private UserControl? GetStaticViewByKey(string key) => _viewRegistry.GetStaticView(key);



    private TabItem? GetStaticTabByKey(string key)

    {

        return key switch

        {

            "SystemInfo" => SystemInfoTab,

            "StaticProcess" => StaticProcessTab,

            "RecentFiles" => RecentFilesTab,

            "Prefetch" => PrefetchTab,

            "Amcache" => AmcacheTab,

            "Autorun" => AutorunTab,

            "EventLog" => EventLogTab,

            "Network" => NetworkTab,

            "BrowsingHistory" => BrowsingHistoryTab,

            "MFT" => MftTab,

            _ => null

        };

    }



    private void StaticTabContextMenu_Opened(object sender, RoutedEventArgs e)

    {

        if (sender is not System.Windows.Controls.ContextMenu cm || cm.PlacementTarget is not System.Windows.Controls.TabItem tab)

            return;

        var key = tab.Tag as string;

        if (string.IsNullOrEmpty(key))

            return;

        var detached = IsStaticTabDetached(key);

        if (cm.Items.Count >= 2)

        {

            if (cm.Items[0] is System.Windows.Controls.MenuItem detachItem)

                detachItem.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;

            if (cm.Items[1] is System.Windows.Controls.MenuItem restoreItem)

                restoreItem.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;

        }

    }



    private void StaticTabDetachMenuItem_Click(object sender, RoutedEventArgs e)

    {

        if (sender is not System.Windows.Controls.MenuItem mi || mi.Parent is not System.Windows.Controls.ContextMenu cm || cm.PlacementTarget is not System.Windows.Controls.TabItem tab)

            return;

        var key = tab.Tag as string;

        if (!string.IsNullOrEmpty(key) && !IsStaticTabDetached(key))

            DetachStaticTab(key);

    }



    private void StaticTabRestoreMenuItem_Click(object sender, RoutedEventArgs e)

    {

        if (sender is not System.Windows.Controls.MenuItem mi || mi.Parent is not System.Windows.Controls.ContextMenu cm || cm.PlacementTarget is not System.Windows.Controls.TabItem tab)

            return;

        var key = tab.Tag as string;

        if (!string.IsNullOrEmpty(key) && IsStaticTabDetached(key))

            RestoreStaticTab(key);

    }



    private ToolBar CreateStaticDetachedToolBar(string key)

    {

        var toolBar = new ToolBar();

        var restoreBtn = new Button

        {

            Content = new System.Windows.Shapes.Path

            {

                Data = TryFindResource("RestoreIcon") as System.Windows.Media.Geometry,

                Stretch = System.Windows.Media.Stretch.Uniform,

                Width = 18,

                Height = 18

            },

            ToolTip = "Restore to Main Window",

            Margin = new Thickness(5, 0, 5, 0)

        };

        restoreBtn.Click += (_, __) => RestoreStaticTab(key);

        toolBar.Items.Add(restoreBtn);

        return toolBar;

    }



    private void DetachStaticTab(string key, LayoutPersistence.DetachedTabLayout? detachedLayout = null)

    {

        if (_viewRegistry.TryGetDetachedStatic(key, out var existingWindow, out _))

        {

            existingWindow!.Activate();

            return;

        }



        var view = GetStaticViewByKey(key);

        if (view == null)

            return;



        var window = new DetachedTabWindow(GetStaticTabTitle(key), view) { Owner = this };

        ApplyDetachedLayout(window, detachedLayout);

        window.SetToolBar(CreateStaticDetachedToolBar(key));

        window.RestoreRequested += (_, restoredView) =>

        {

            RestoreStaticTab(key, restoredView, closeWindow: false);

        };



        _viewRegistry.SetDetachedStatic(key, window, view);

        window.Show();



        if (StaticTabControl?.SelectedItem == GetStaticTabByKey(key))

            SharedContentArea.Content = CreateDetachedPlaceholder(GetStaticTabTitle(key));



        UpdateDetachButtonState();

    }



    private void RestoreStaticTab(string key, UserControl? restoredView = null, bool closeWindow = true)

    {

        if (!_viewRegistry.TryGetDetachedStatic(key, out var window, out var view))

            return;



        if (closeWindow && window!.IsVisible)

        {

            window.SuppressRestore = true;

            window.Close();

        }



        var viewToShow = restoredView ?? view;

        if (viewToShow == null)

            return;



        _viewRegistry.RestoreStatic(key);



        if (StaticTabControl?.SelectedItem == GetStaticTabByKey(key))

            SharedContentArea.Content = viewToShow;



        UpdateDetachButtonState();

    }



    private List<LayoutPersistence.DetachedTabLayout> GetDetachedDynamicLayouts()

    {

        var list = new List<LayoutPersistence.DetachedTabLayout>();

        foreach (var item in _viewRegistry.GetDetachedDynamicTabs())

        {

            list.Add(new LayoutPersistence.DetachedTabLayout

            {

                Key = item.Key,

                Left = item.Window.Left,

                Top = item.Window.Top,

                Width = item.Window.Width,

                Height = item.Window.Height

            });

        }



        return list;

    }



    private List<LayoutPersistence.DetachedTabLayout> GetDetachedStaticLayouts()

    {

        var list = new List<LayoutPersistence.DetachedTabLayout>();

        foreach (var item in _viewRegistry.GetDetachedStaticTabs())

        {

            list.Add(new LayoutPersistence.DetachedTabLayout

            {

                Key = item.Key,

                Left = item.Window.Left,

                Top = item.Window.Top,

                Width = item.Window.Width,

                Height = item.Window.Height

            });

        }



        return list;

    }



    private static void ApplyDetachedLayout(DetachedTabWindow window, LayoutPersistence.DetachedTabLayout? layout)

    {

        if (layout == null)

            return;

        if (layout.Width > 200)

            window.Width = layout.Width;

        if (layout.Height > 150)

            window.Height = layout.Height;

        if (!double.IsNaN(layout.Left) && !double.IsNaN(layout.Top))

        {

            window.WindowStartupLocation = WindowStartupLocation.Manual;

            window.Left = layout.Left;

            window.Top = layout.Top;

        }

    }



    private ToolBar CreateDetachedToolBar(string key)

    {

        var toolBar = new ToolBar { Padding = new Thickness(5) };

        var source = DynamicToolBar;



        void AddIconButton(string geometryKey, string styleKey, string tooltip, RoutedEventHandler handler)

        {

            var geom = source?.FindResource(geometryKey) as Geometry;

            var style = source?.FindResource(styleKey) as Style;

            if (geom == null) return;

            var path = new System.Windows.Shapes.Path

            {

                Data = geom,

                Stretch = Stretch.Uniform,

                Width = 18,

                Height = 18

            };

            if (style != null) path.Style = style;

            var button = new Button

            {

                Content = path,

                Margin = new Thickness(5, 0, 5, 0),

                ToolTip = tooltip

            };

            button.Click += handler;

            toolBar.Items.Add(button);

        }



        ToggleButton AddIconToggle(string imageResourceKey, string tooltip, bool isChecked, RoutedEventHandler checkedHandler)

        {

            var img = source?.FindResource(imageResourceKey) as ImageSource;

            var content = img != null ? (object)new Image { Source = img, Width = 18, Height = 18 } : null;

            var toggle = new ToggleButton

            {

                Content = content,

                IsChecked = isChecked,

                Margin = new Thickness(5, 0, 5, 0),

                ToolTip = tooltip

            };

            toggle.Checked += checkedHandler;

            toggle.Unchecked += checkedHandler;

            toolBar.Items.Add(toggle);

            return toggle;

        }



        ToggleButton? AddProtocolToggle(string geometryKey, string tooltip, bool isChecked, RoutedEventHandler checkedHandler)

        {

            var geom = source?.FindResource(geometryKey) as Geometry;

            if (geom == null) return null;

            var path = new System.Windows.Shapes.Path

            {

                Data = geom,

                Stroke = new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)),

                StrokeThickness = 2,

                Stretch = Stretch.Uniform,

                Width = 16,

                Height = 16

            };

            var toggle = new ToggleButton

            {

                Content = path,

                IsChecked = isChecked,

                Margin = new Thickness(5, 0, 5, 0),

                ToolTip = tooltip

            };

            toggle.Checked += checkedHandler;

            toggle.Unchecked += checkedHandler;

            toolBar.Items.Add(toggle);

            return toggle;

        }



        if (key == "Timeline")

        {

            AddIconButton("PlayIcon", "ClearIconStyle", "Play/Pause", (_, e) => StartButton_Click(TimelineStartButton, e));

            AddIconButton("ClearTrashCanGeometry", "ClearIconStyle", "Clear", TimelineClearButton_Click);

        }

        else if (key == "LiveStream")

        {

            AddIconButton("PlayIcon", "ClearIconStyle", "Play/Pause", (_, e) => StartButton_Click(LiveStreamStartButton, e));

            AddIconButton("ClearTrashCanGeometry", "ClearIconStyle", "Clear", LiveStreamClearButton_Click);

        }

        else if (key == "Process")

        {

            AddIconButton("PlayIcon", "ClearIconStyle", "Play/Pause", (_, e) => StartButton_Click(ProcessStartButton, e));

            AddIconButton("RefreshIcon", "RefreshIconStyle", "Refresh", ProcessRefreshButton_Click);

            AddIconButton("ClearTrashCanGeometry", "ClearIconStyle", "Clear", ProcessClearButton_Click);

        }

        else if (key == "LiveTcp")

        {

            AddIconButton("PlayIcon", "ClearIconStyle", "Play/Pause", (_, e) => StartButton_Click(LiveTcpStartButton, e));

            AddIconButton("RefreshIcon", "RefreshIconStyle", "Refresh", LiveTcpRefreshButton_Click);

            AddIconButton("ClearTrashCanGeometry", "ClearIconStyle", "Clear", LiveTcpClearButton_Click);



            ToggleButton? resolve = null;

            resolve = AddIconToggle("ResolveIconImage", "Resolve IP/FQDN", LiveTcpResolveButton.IsChecked == true, (_, e) =>

            {

                LiveTcpResolveButton.IsChecked = resolve!.IsChecked;

                LiveTcpResolveToggle_Checked(LiveTcpResolveButton, e);

            });

            var statesBtn = new Button

            {

                Content = source?.FindResource("StateIconImage") is ImageSource stateImg ? new Image { Source = stateImg, Width = 18, Height = 18 } : null,

                Margin = new Thickness(5, 0, 5, 0),

                ToolTip = "States Filter"

            };

            statesBtn.Click += (_, e) => LiveTcpStateFilterButton_Click(LiveTcpStateFilterButton, e);

            toolBar.Items.Add(statesBtn);



            ToggleButton? tcpV4 = null, tcpV6 = null, udpV4 = null, udpV6 = null;

            tcpV4 = AddProtocolToggle("TcpIcon", "TCP v4", LiveTcpTcpV4ToggleButton.IsChecked == true, (_, e) =>

            {

                if (tcpV4 != null)

                    LiveTcpTcpV4ToggleButton.IsChecked = tcpV4.IsChecked;

                LiveTcpProtocolToggle_Checked(LiveTcpTcpV4ToggleButton, e);

            });

            tcpV6 = AddProtocolToggle("TcpIcon", "TCP v6", LiveTcpTcpV6ToggleButton.IsChecked == true, (_, e) =>

            {

                if (tcpV6 != null)

                    LiveTcpTcpV6ToggleButton.IsChecked = tcpV6.IsChecked;

                LiveTcpProtocolToggle_Checked(LiveTcpTcpV6ToggleButton, e);

            });

            udpV4 = AddProtocolToggle("UdpIcon", "UDP v4", LiveTcpUdpV4ToggleButton.IsChecked == true, (_, e) =>

            {

                if (udpV4 != null)

                    LiveTcpUdpV4ToggleButton.IsChecked = udpV4.IsChecked;

                LiveTcpProtocolToggle_Checked(LiveTcpUdpV4ToggleButton, e);

            });

            udpV6 = AddProtocolToggle("UdpIcon", "UDP v6", LiveTcpUdpV6ToggleButton.IsChecked == true, (_, e) =>

            {

                if (udpV6 != null)

                    LiveTcpUdpV6ToggleButton.IsChecked = udpV6.IsChecked;

                LiveTcpProtocolToggle_Checked(LiveTcpUdpV6ToggleButton, e);

            });

        }



        toolBar.Items.Add(new Separator { Margin = new Thickness(8, 0, 8, 0) });

        AddIconButton("RestoreIcon", "ClearIconStyle", "Restore", (_, __) => RestoreDetachedTab(key));



        return toolBar;

    }



    private void OnEventProduced(object? sender, ActivityEvent activityEvent)

    {

        var normalizedEvent = ActivityEventNormalizer.Normalize(activityEvent);



        // Add to index

        _index.AddEvent(normalizedEvent);



        var newCount = Interlocked.Increment(ref _uiEventCount);

        if (newCount > MaxUiEvents)

        {

            Interlocked.Decrement(ref _uiEventCount);

            Interlocked.Increment(ref _uiDroppedEvents);

            return;

        }



        _pendingUiEvents.Enqueue(normalizedEvent);

        if (Interlocked.Exchange(ref _uiFlushScheduled, 1) == 0)

        {

            Dispatcher.BeginInvoke(new Action(FlushUiEvents));

        }

    }



    private void FlushUiEvents()

    {

        try

        {

            var processedThisFlush = 0;

            while (processedThisFlush < MaxUiEventsPerFlushIteration && _pendingUiEvents.TryDequeue(out var evt))

            {

                processedThisFlush++;

                Interlocked.Decrement(ref _uiEventCount);



                if (evt.Category == "Browser" && BrowsingHistoryViewControl != null)

                {

                    var nowBrowserUtc = DateTime.UtcNow;

                    if ((nowBrowserUtc - _lastBrowserRefreshUtc).TotalMilliseconds >= 1000)

                    {

                        _lastBrowserRefreshUtc = nowBrowserUtc;

                        BrowsingHistoryViewControl.Refresh();

                    }

                }



                if (DynamicTabControl?.SelectedItem == LiveStreamTab &&

                    LiveStreamViewControl != null &&

                    _isCollecting &&

                    !_isLiveStreamPaused &&

                    !LiveStreamViewControl.IsPaused)

                {

                    LiveStreamViewControl.AddEvent(evt);

                }

            }



            var nowUtc = DateTime.UtcNow;

            if ((nowUtc - _lastRefreshUtc).TotalMilliseconds >= 1000)

            {

                _lastRefreshUtc = nowUtc;

                ReportIndexEvictions(nowUtc);

                TrimIndexQueuesIfNeeded(nowUtc);

                UpdateIndexStatus(nowUtc);

                if (_showStatusBarDiagnostics)
                {
                    UpdateProcessCacheStatus(nowUtc);
                    UpdateEtwThrottleStatus(nowUtc);
                    UpdateUiBackpressureStatus(nowUtc);
                }

                UpdateCollectionWarnings();

                if (!_isDynamicPaused && DynamicTabControl?.SelectedItem == TimelineTab && TimelineViewControl != null)

                    TimelineViewControl.Refresh();

                if (!_isDynamicPaused && DynamicTabControl?.SelectedItem == ProcessTab && ProcessViewControl != null)

                    ProcessViewControl.Refresh();

                if (!_isDynamicPaused && DynamicTabControl?.SelectedItem == LiveTcpTab && LiveTcpViewControl != null)

                    LiveTcpViewControl.Refresh();

                if (StaticTabControl?.SelectedItem == RecentFilesTab && RecentFilesViewControl != null)

                    RecentFilesViewControl.Refresh();

                if (StaticTabControl?.SelectedItem == PrefetchTab && PrefetchViewControl != null)

                    PrefetchViewControl.Refresh();

                if (StaticTabControl?.SelectedItem == AmcacheTab && AmcacheViewControl != null)

                    AmcacheViewControl.Refresh();

                if (StaticTabControl?.SelectedItem == AutorunTab && AutorunViewControl != null)

                    AutorunViewControl.Refresh();

                if (StaticTabControl?.SelectedItem == EventLogTab && EventLogViewControl != null)

                    EventLogViewControl.Refresh();

                if (StaticTabControl?.SelectedItem == NetworkTab && NetworkViewControl != null)

                    NetworkViewControl.Refresh();

                if (StaticTabControl?.SelectedItem == BrowsingHistoryTab && BrowsingHistoryViewControl != null)

                    BrowsingHistoryViewControl.Refresh();

            }

        }

        finally

        {

            Interlocked.Exchange(ref _uiFlushScheduled, 0);

            if (ShouldScheduleAnotherUiFlush(!_pendingUiEvents.IsEmpty) &&

                Interlocked.Exchange(ref _uiFlushScheduled, 1) == 0)

            {

                Dispatcher.BeginInvoke(new Action(FlushUiEvents));

            }

        }

    }



    private async void StartButton_Click(object sender, RoutedEventArgs e)

    {

        try

        {

            // Determine which view's Start button was clicked

            bool isLiveStream = sender == LiveStreamStartButton;

            

            // Update all buttons to reflect collecting state

            TimelineStartButton.IsEnabled = true;

            ProcessStartButton.IsEnabled = true;

            LiveStreamStartButton.IsEnabled = true;

            LiveTcpStartButton.IsEnabled = true;



            await StartProvidersAsync();



            if (isLiveStream)

            {

                _isLiveStreamPaused = !_isLiveStreamPaused;

                UpdatePlayPauseButtonIcon(LiveStreamStartButton, _isLiveStreamPaused);



                if (LiveStreamViewControl != null)

                {

                    if (_isLiveStreamPaused)

                        LiveStreamViewControl.Pause();

                    else

                        LiveStreamViewControl.Resume();

                }

            }

            else

            {

                _isDynamicPaused = !_isDynamicPaused;

                UpdatePlayPauseButtonIcon(TimelineStartButton, _isDynamicPaused);

                UpdatePlayPauseButtonIcon(ProcessStartButton, _isDynamicPaused);

                UpdatePlayPauseButtonIcon(LiveTcpStartButton, _isDynamicPaused);

            }

        }

        catch (Exception ex)

        {

            MessageBox.Show($"Error starting providers: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            

            // Reset all buttons on error

            TimelineStartButton.IsEnabled = true;

            ProcessStartButton.IsEnabled = true;

            LiveStreamStartButton.IsEnabled = true;

            LiveTcpStartButton.IsEnabled = true;

            

            _isCollecting = false;

            _isDynamicPaused = true;

            _isLiveStreamPaused = true;

            UpdatePlayPauseButtonIcon(TimelineStartButton, true);

            UpdatePlayPauseButtonIcon(ProcessStartButton, true);

            UpdatePlayPauseButtonIcon(LiveStreamStartButton, true);

            UpdatePlayPauseButtonIcon(LiveTcpStartButton, true);

        }

    }



    private void TimelineClearButton_Click(object sender, RoutedEventArgs e)

    {

        TimelineViewControl?.Clear();

    }



    private void ProcessClearButton_Click(object sender, RoutedEventArgs e)

    {

        ProcessViewControl?.Clear();

    }



    private void ProcessRefreshButton_Click(object sender, RoutedEventArgs e)

    {

        ProcessViewControl?.Refresh(true);

    }



    private IActivityIndex? _baselineIndex;

    private IActivityIndex? _snapshotIndex;

    private string? _snapshotPath;



    private async void OpenSnapshotMenuItem_Click(object sender, RoutedEventArgs e)

    {

        try

        {

            var dialog = new OpenFileDialog

            {

                Title = "Select snapshot folder (choose timeline.json in the snapshot folder)",

                Filter = "timeline.json|timeline.json|All files (*.*)|*.*",

                FileName = "timeline.json",

                CheckFileExists = false

            };

            if (dialog.ShowDialog() != true)

                return;

            var folderPath = Path.GetDirectoryName(dialog.FileName);

            if (string.IsNullOrEmpty(folderPath))

                return;

            Mouse.OverrideCursor = Cursors.Wait;

            var integrityResult = await SnapshotIntegrityVerifier.VerifyFolderAsync(folderPath);
            Mouse.OverrideCursor = null;

            if (!ConfirmOpenSnapshotAfterIntegrityCheck(integrityResult))
                return;

            Mouse.OverrideCursor = Cursors.Wait;

            var index = await SnapshotImporter.LoadFromFolderAsync(folderPath);

            Mouse.OverrideCursor = null;

            if (index is SnapshotActivityIndex snapshot && snapshot.EventCount == 0)

            {

                MessageBox.Show("No events found in this snapshot.\n\nCheck that manifest.json and timeline.json are present and that the export completed without errors.", "Open Snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;

            }

            _snapshotIndex = index;

            _snapshotPath = folderPath;

            TimelineViewControl?.SetSnapshotIndex(index);

            CloseSnapshotMenuItem.Visibility = Visibility.Visible;

            _mainViewModel.SelectedDynamicTabIndex = 0;

            if (DynamicTabControl != null)

                DynamicTabControl.SelectedItem = TimelineTab;

            UpdateSharedContent();

            MessageBox.Show($"Loaded {(_snapshotIndex is SnapshotActivityIndex sa ? sa.EventCount : 0)} events from snapshot.", "Open Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            Mouse.OverrideCursor = null;

            var hint = ex is UnauthorizedAccessException or System.Security.SecurityException
                ? "\n\nCheck that the snapshot folder is readable and that required files are not blocked by permissions."
                : ex is IOException
                    ? "\n\nCheck that the snapshot files are not locked by another process and that the storage device is still available."
                    : "";

            MessageBox.Show($"Failed to open snapshot.\n\n{ex.Message}{hint}", "Open Snapshot", MessageBoxButton.OK, MessageBoxImage.Error);

        }

    }



    private static bool ConfirmOpenSnapshotAfterIntegrityCheck(SnapshotIntegrityVerificationResult integrityResult)
    {
        if (integrityResult.Status == SnapshotIntegrityStatus.Verified)
            return true;

        var title = integrityResult.Status == SnapshotIntegrityStatus.Unverified
            ? "Snapshot Not Verified"
            : "Snapshot Integrity Failed";
        var intro = integrityResult.Status == SnapshotIntegrityStatus.Unverified
            ? "Snapshot integrity could not be verified."
            : "Snapshot integrity verification failed.";
        var issueLines = integrityResult.Issues.Count > 0
            ? string.Join("\n", integrityResult.Issues.Take(5))
            : "No details were reported.";
        var truncatedSuffix = integrityResult.Issues.Count > 5
            ? $"\n...and {integrityResult.Issues.Count - 5} more issue(s)."
            : string.Empty;

        var message = $"{intro}\n\n{issueLines}{truncatedSuffix}\n\nOpen snapshot anyway?";
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }



    private async void ExportToSqliteMenuItem_Click(object sender, RoutedEventArgs e)

    {

        try

        {

            var effectiveIndex = _snapshotIndex ?? _index;

            var dialog = new SaveFileDialog

            {

                Title = "Export to SQLite",

                Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",

                DefaultExt = "db",

                FileName = "timeline.db"

            };

            if (dialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;

            var count = await Task.Run(() => SqliteIndexPersistence.Export(effectiveIndex, dialog.FileName));

            Mouse.OverrideCursor = null;

            MessageBox.Show($"Exported {count} events to {dialog.FileName}", "Export to SQLite", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            Mouse.OverrideCursor = null;

            LogToAppLog("Export to SQLite failed", ex);

            MessageBox.Show($"Export failed: {ex.Message}", "Export to SQLite", MessageBoxButton.OK, MessageBoxImage.Error);

        }

    }



    private async void OpenFromSqliteMenuItem_Click(object sender, RoutedEventArgs e)

    {

        try

        {

            var dialog = new OpenFileDialog

            {

                Title = "Open from SQLite",

                Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",

                FileName = "timeline.db"

            };

            if (dialog.ShowDialog() != true) return;

            Mouse.OverrideCursor = Cursors.Wait;

            var dbPath = dialog.FileName;

            var events = await Task.Run(() =>

            {

                var all = new List<ActivityEvent>();

                const int pageSize = 20_000;

                var offset = 0;

                while (true)

                {

                    var page = SqliteIndexPersistence.LoadEventsPage(

                        dbPath,

                        DateTime.MinValue,

                        DateTime.MaxValue,

                        offset,

                        pageSize);

                    if (page.Count == 0)

                        break;

                    all.AddRange(page);

                    offset += page.Count;

                }



                return all;

            });

            if (events.Count == 0)

            {

                MessageBox.Show("No events found in this database or file is invalid.", "Open from SQLite", MessageBoxButton.OK, MessageBoxImage.Warning);

                return;

            }

            var index = new SnapshotActivityIndex(events);

            _snapshotIndex = index;

            _snapshotPath = dialog.FileName;

            TimelineViewControl?.SetSnapshotIndex(index);

            CloseSnapshotMenuItem.Visibility = Visibility.Visible;

            _mainViewModel.SelectedDynamicTabIndex = 0;

            if (DynamicTabControl != null)

                DynamicTabControl.SelectedItem = TimelineTab;

            UpdateSharedContent();

            MessageBox.Show($"Loaded {events.Count} events from SQLite.", "Open from SQLite", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            LogToAppLog("Open from SQLite failed", ex);

            MessageBox.Show($"Load failed: {ex.Message}", "Open from SQLite", MessageBoxButton.OK, MessageBoxImage.Error);

        }

        finally

        {

            Mouse.OverrideCursor = null;

        }

    }



    private void CloseSnapshotMenuItem_Click(object sender, RoutedEventArgs e)

    {

        _snapshotIndex = null;

        _snapshotPath = null;

        TimelineViewControl?.SetSnapshotIndex(_index);

        CloseSnapshotMenuItem.Visibility = Visibility.Collapsed;

        TimelineViewControl?.Refresh();

    }



    private SnapshotExportOptions? BuildSnapshotExportOptions(long? sourceEventCount = null, PreflightReport? preflightReport = null)
    {
        var etwProvider = _providers.OfType<IEtwThrottleStatsProvider>().FirstOrDefault();
        var etwStats = etwProvider?.GetEtwThrottleStats();
        var useVssSnapshots = _snapshotExporter is SnapshotExporter exporter && exporter.UseVssSnapshots;
        var extras = CollectionMetadataBuilder.BuildBaseManifestExtras(
            _runtimeSettings,
            executionContext: "ui_interactive",
            useVssSnapshots: useVssSnapshots,
            enabledProviders: _providers.Select(p => p.GetType().Name),
            preflightReport: preflightReport);
        extras["toolVersion"] = ToolVersionProvider.GetCurrentVersion(typeof(MainWindow));

        if (etwStats != null && etwStats.TotalDrops.Count > 0)
            extras["etwTotalDrops"] = etwStats.TotalDrops;

        var uiDroppedTotal = Interlocked.Read(ref _uiDroppedEvents);
        extras["uiBackpressureDroppedTotal"] = uiDroppedTotal;
        extras["knownLimitations"] = new[]
        {
            "File locking: VSS snapshot used where possible; fallback to live path. Admin + VSS service required.",
            "Rootkit/API hooking: Results may be affected if raw registry/MFT parsing is not used; live API can be hooked.",
            "ETW throttling: High-frequency events may be dropped; see etwTotalDrops in this manifest if present.",
            "UI backpressure: when UI queue is saturated, excess UI-render events are dropped; see uiBackpressureDroppedTotal."
        };
        return new SnapshotExportOptions
        {
            ManifestExtras = extras,
            SourceEventCount = sourceEventCount,
            CollectionSummaryExtras = BuildSnapshotCollectionSummaryExtras(preflightReport, etwStats, uiDroppedTotal)
        };
    }

    private static IReadOnlyDictionary<string, object?> BuildSnapshotCollectionSummaryExtras(
        PreflightReport? preflightReport,
        EtwThrottleStats? etwStats,
        long uiDroppedTotal)
    {
        var extras = new Dictionary<string, object?>
        {
            ["uiBackpressureDroppedTotal"] = uiDroppedTotal
        };

        if (preflightReport != null)
        {
            extras["preflightWarningCount"] = preflightReport.Warnings.Length;
            extras["preflightErrorCount"] = preflightReport.Errors.Length;
        }

        if (etwStats != null)
        {
            long etwDroppedEventTotal = etwStats.TotalDrops.Values.Sum(static value => (long)value);
            if (etwDroppedEventTotal > 0)
                extras["etwDroppedEventTotal"] = etwDroppedEventTotal;
        }

        return extras;
    }

    private PreflightReport BuildSnapshotPreflightReport(string outputDirectory)
    {
        var useVssSnapshots = _snapshotExporter is SnapshotExporter exporter && exporter.UseVssSnapshots;
        return PreflightReportBuilder.Build(
            _runtimeSettings,
            executionContext: "ui_interactive",
            useVssSnapshots: useVssSnapshots,
            enabledProviders: _providers.Select(p => p.GetType().Name),
            outputDirectory: outputDirectory);
    }

    private static bool ShowSnapshotPreflightGate(PreflightReport report)
    {
        if (report.HasErrors)
        {
            MessageBox.Show(
                BuildSnapshotPreflightMessage(report, includeContinuePrompt: false),
                "Export Snapshot Preflight",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var operationalWarnings = new List<string>();
        if (report.OutputDirectoryWritable == false)
            operationalWarnings.Add("Output directory is not writable.");

        if (report.AvailableFreeSpaceBytes.HasValue
            && report.AvailableFreeSpaceBytes.Value < report.MinimumRecommendedFreeSpaceBytes)
        {
            operationalWarnings.Add(
                $"Available free space is below the recommended minimum ({FormatBytes(report.AvailableFreeSpaceBytes.Value)} available).");
        }

        if (report.UseVssSnapshots && !report.VssServiceRunning)
            operationalWarnings.Add("VSS-backed export is enabled, but the Volume Shadow Copy service is not running.");

        if (report.UseVssSnapshots && !report.IsAdministrator)
            operationalWarnings.Add("Administrator privileges are recommended for VSS-backed export.");

        var warnings = PreflightReportBuilder.MergeWarnings(report.Warnings, operationalWarnings);
        if (warnings.Length == 0)
            return true;

        MessageBoxResult result = MessageBox.Show(
            BuildSnapshotPreflightMessage(report, includeContinuePrompt: true, additionalWarnings: operationalWarnings),
            "Export Snapshot Preflight",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
    }

    private static string BuildSnapshotPreflightMessage(
        PreflightReport report,
        bool includeContinuePrompt,
        IReadOnlyCollection<string>? additionalWarnings = null)
    {
        var lines = new List<string>();
        if (report.Errors.Length > 0)
        {
            lines.Add("Preflight errors:");
            lines.AddRange(report.Errors.Select(error => $"- {error}"));
        }

        var warnings = PreflightReportBuilder.MergeWarnings(report.Warnings, additionalWarnings);
        if (warnings.Length > 0)
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);

            lines.Add("Preflight warnings:");
            lines.AddRange(warnings.Select(warning => $"- {warning}"));
        }

        if (!string.IsNullOrWhiteSpace(report.OutputDirectory))
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);

            lines.Add($"Output directory: {report.OutputDirectory}");
        }

        if (includeContinuePrompt)
        {
            lines.Add(string.Empty);
            lines.Add("Continue export?");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)

    {

        try

        {

            var dialog = new SaveFileDialog

            {

                Filter = "Snapshot Directory|*",

                FileName = "snapshot",

                Title = "Export Snapshot"

            };



            if (dialog.ShowDialog() == true)
            {
                var outputDir = Path.GetDirectoryName(dialog.FileName);
                if (string.IsNullOrEmpty(outputDir))
                    return;

                var preflightReport = BuildSnapshotPreflightReport(outputDir);
                if (!ShowSnapshotPreflightGate(preflightReport))
                    return;

                Mouse.OverrideCursor = Cursors.Wait;

                var snapshotIndex = await Task.Run(() =>
                {
                    var exportIndex = new InMemoryActivityIndex(0);
                    SnapshotMerger.MergeLiveToSnapshot(_index, exportIndex);
                    return exportIndex;
                }).ConfigureAwait(true);

                var exportOptions = BuildSnapshotExportOptions(snapshotIndex.EventCount, preflightReport);
                await Task.Run(() => _snapshotExporter.ExportAsync(snapshotIndex, outputDir, exportOptions)).ConfigureAwait(true);
                Mouse.OverrideCursor = null;
// Create diff if baseline exists

                if (_baselineIndex != null)

                {

                    var diff = SnapshotMerger.CreateDiff(_baselineIndex, snapshotIndex);

                    

                    // Apply diff highlighting to views

                    if (ProcessViewControl != null)

                    {

                        ProcessViewControl.Refresh();

                        var processViewModel = ProcessViewControl.DataContext as ProcessViewModel;

                        processViewModel?.ApplyDiff(diff);

                    }

                    

                    if (NetworkViewControl != null)

                    {

                        NetworkViewControl.Refresh();

                        var networkViewModel = NetworkViewControl.DataContext as NetworkViewModel;

                        networkViewModel?.ApplyDiff(diff);

                    }

                }

                _baselineIndex = snapshotIndex;



                MessageBox.Show($"Snapshot exported successfully to:\n{outputDir}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                if (!_runtimeSettings.Ui.HasSeenExportSnapshotTip)

                {

                    MessageBox.Show(

                        "\u532f\u51fa\u5167\u5bb9\u542b knownLimitations \u7b49\u6b04\u4f4d\uff1b\u64b0\u5beb\u9451\u8b58\u5831\u544a\u6642\u8acb\u4e00\u4f75\u63d0\u4f9b manifest.json\uff0c\u4e26\u5f15\u7528 LIMITATIONS \u8aaa\u660e\u3002",

                        "\u532f\u51fa Snapshot \u8aaa\u660e",

                        MessageBoxButton.OK,

                        MessageBoxImage.Information);

                    _runtimeSettings.Ui.HasSeenExportSnapshotTip = true;

                    HostWitnessSettings.Save(_runtimeSettings);

                }

            }

        }

        catch (Exception ex)

        {

            Mouse.OverrideCursor = null;

            LogToAppLog("Export Snapshot failed", ex);

            var hint = ex is UnauthorizedAccessException or System.Security.SecurityException
                ? "\n\nCheck that the output folder is writable and that the current process can access required files."
                : ex is IOException
                    ? "\n\nCheck that the output path is available and that snapshot files are not locked by another process."
                    : "";

            if (ex.Message.IndexOf("VSS", StringComparison.OrdinalIgnoreCase) >= 0 || ex.Message.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                hint += "\n\nVSS errors may indicate that Volume Shadow Copy is unavailable or that the current process lacks the required privileges.";

            MessageBox.Show($"Failed to export snapshot.\n\n{ex.Message}{hint}", "Export Snapshot", MessageBoxButton.OK, MessageBoxImage.Error);

        }

    }



    private void DynamicTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)

    {

        if (DynamicTabControl == null || StaticTabControl == null || SharedContentArea == null || _isTabSwitching) return;



        if (DynamicTabControl.SelectedIndex != -1)

        {

            _isTabSwitching = true;

            _mainViewModel.SelectedStaticTabIndex = -1;

            _isTabSwitching = false;



            UpdateSharedContent();

            UpdateDrillDownMenuItemState();



            if (DynamicTabControl.SelectedItem == TimelineTab) TimelineViewControl?.Refresh();

            else if (DynamicTabControl.SelectedItem == ProcessTab) ProcessViewControl?.Refresh();

            else if (DynamicTabControl.SelectedItem == LiveTcpTab) LiveTcpViewControl?.Refresh();

            else if (DynamicTabControl.SelectedItem == LiveStreamTab)

            {

                if (LiveStreamViewControl != null && !LiveStreamViewControl.IsPaused && _isLiveStreamPaused)

                    LiveStreamViewControl.Pause();

            }

        }

    }



    private void UpdateDrillDownMenuItemState()

    {

        if (DrillDownMenuItem == null) return;

        var onProcessTab = DynamicTabControl?.SelectedItem == ProcessTab;

        DrillDownMenuItem.IsEnabled = onProcessTab;

    }



    private void StaticTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)

    {

        if (DynamicTabControl == null || StaticTabControl == null || SharedContentArea == null || _isTabSwitching) return;



        if (StaticTabControl.SelectedIndex != -1)

        {

            _isTabSwitching = true;

            _mainViewModel.SelectedDynamicTabIndex = -1;

            _isTabSwitching = false;



            UpdateSharedContent();

            

            if (StaticTabControl.SelectedItem == SystemInfoTab) SystemInfoViewControl?.Refresh();

            else if (StaticTabControl.SelectedItem == RecentFilesTab) RecentFilesViewControl?.Refresh();

            else if (StaticTabControl.SelectedItem == PrefetchTab) PrefetchViewControl?.Refresh();

            else if (StaticTabControl.SelectedItem == AmcacheTab) AmcacheViewControl?.Refresh();

            else if (StaticTabControl.SelectedItem == AutorunTab) AutorunViewControl?.Refresh();

            else if (StaticTabControl.SelectedItem == EventLogTab) EventLogViewControl?.Refresh();

            else if (StaticTabControl.SelectedItem == NetworkTab) NetworkViewControl?.Refresh();

            else if (StaticTabControl.SelectedItem == BrowsingHistoryTab) BrowsingHistoryViewControl?.Refresh();

        }

    }



    private async Task StartProvidersAsync()

    {

        if (_isCollecting)

        {

            return;

        }



        try

        {

            _isCollecting = true;

            _ = CollectionWarnings.SnapshotAndClear(); // clear previous run warnings

            await ProviderLifecycleHelper.StartProvidersAsync(_providers);



            // Refresh static views after providers start

            Dispatcher.Invoke(() =>

            {

                StaticProcessViewControl?.Refresh();

                RecentFilesViewControl?.Refresh();

                PrefetchViewControl?.Refresh();

                AmcacheViewControl?.Refresh();

                AutorunViewControl?.Refresh();

                EventLogViewControl?.Refresh();

                NetworkViewControl?.Refresh();

                BrowsingHistoryViewControl?.Refresh();

            });

        }

        catch (ProviderStartException ex)
        {
            LogToAppLog("Provider start failed", ex.StartException);
            LogProviderLifecycleExceptions("Provider rollback failed", ex.StopExceptions);

            var message = $"Error starting providers: {ex.StartException.Message}";
            if (ex.StopExceptions.Count > 0)
                message += "\n\nOne or more providers also failed to stop during rollback. See the application log for details.";

            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            _isCollecting = false;
        }

    }



    // EventsDataGrid_SelectionChanged removed - using TimelineView instead



    private void LiveStreamClearButton_Click(object sender, RoutedEventArgs e)

    {

        LiveStreamViewControl?.Clear();

    }



    private void LiveTcpClearButton_Click(object sender, RoutedEventArgs e)

    {

        LiveTcpViewControl?.Clear();

    }



    private void LiveTcpRefreshButton_Click(object sender, RoutedEventArgs e)

    {

        LiveTcpViewControl?.Refresh();

    }



    private void StaticProcessRefreshButton_Click(object sender, RoutedEventArgs e)

    {

        StaticProcessViewControl?.Refresh(true);

    }



    private void NetworkRefreshButton_Click(object sender, RoutedEventArgs e)

    {

        NetworkViewControl?.Refresh();

    }



    private async void LiveTcpResolveToggle_Checked(object sender, RoutedEventArgs e)

    {

        if (LiveTcpViewControl == null)

            return;



        _isLiveTcpResolveEnabled = LiveTcpResolveButton.IsChecked == true;

        try

        {

            await LiveTcpViewControl.ToggleResolveAddressAsync(_isLiveTcpResolveEnabled);

        }

        catch (Exception ex)

        {

            MessageBox.Show($"Resolve toggle failed: {ex.Message}", "Live TCP", MessageBoxButton.OK, MessageBoxImage.Warning);

        }

    }



    private void LiveTcpStateFilterButton_Click(object sender, RoutedEventArgs e)

    {

        if (LiveTcpViewControl == null)

            return;



        var dialog = new LiveTcpStateFilterDialog(LiveTcpViewControl.GetStateFilters())

        {

            Owner = this

        };



        if (dialog.ShowDialog() == true)

        {

            var selected = dialog.GetSelectedStates();

            var allSelected = selected.Count == LiveTcpViewModel.AllStateValues.Length;

            LiveTcpViewControl.SetStateFilters(allSelected ? Array.Empty<string>() : selected);

        }

    }



    private void LiveTcpProtocolToggle_Checked(object sender, RoutedEventArgs e)

    {

        LiveTcpViewControl?.SetProtocolFilters(

            LiveTcpTcpV4ToggleButton.IsChecked == true,

            LiveTcpTcpV6ToggleButton.IsChecked == true,

            LiveTcpUdpV4ToggleButton.IsChecked == true,

            LiveTcpUdpV6ToggleButton.IsChecked == true);

    }



    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)

    {

        Application.Current.Shutdown();

    }



    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)

    {

        var window = new SettingsWindow

        {

            Owner = this

        };

        window.ShowDialog();

    }



    private void RegistrySearchMenuItem_Click(object sender, RoutedEventArgs e)

    {

        if (_registryProvider != null)

        {

            if (!_runtimeSettings.Ui.HasSeenRegistrySearchTip)

            {

                MessageBox.Show(

                    "\u9451\u8b58\u5206\u6790\u8acb\u512a\u5148\u4f7f\u7528 Offline Hive\uff08Settings \u2192 \u50c5\u96e2\u7dda\u767b\u9304\u6a94\uff09\u3002\n\u76ee\u524d\u70ba Live \u67e5\u8a62\uff08\u975e\u9451\u8b58\uff09\uff0c\u50c5\u4f9b\u5373\u6642\u6aa2\u8996\u8207\u8f14\u52a9\u3002",

                    "Registry Search \u8aaa\u660e",

                    MessageBoxButton.OK,

                    MessageBoxImage.Information);

                _runtimeSettings.Ui.HasSeenRegistrySearchTip = true;

                HostWitnessSettings.Save(_runtimeSettings);

            }

        }

        var dialog = new RegistrySearchDialog

        {

            Owner = this,

            Title = _registryProvider == null ? "Registry Search (disabled by policy)" : "Registry Search",

            IsLiveRegistryEnabled = _registryProvider != null,

            RunDefaultQueriesAsync = async (queries, ct) =>

            {

                if (_registryProvider == null || queries == null || queries.Count == 0)

                    return;

                await _registryProvider.RunQueriesAsync(queries, ct);

            },

            RunCustomQueryAsync = async (query, ct) =>

            {

                if (_registryProvider == null)

                    return;

                await _registryProvider.RunQueriesAsync(new[] { query }, ct);

            }

        };

        dialog.ShowDialog();

    }



    private void ExportDiagnosticInfoMenuItem_Click(object sender, RoutedEventArgs e)

    {

        try

        {

            var dialog = new SaveFileDialog

            {

                Title = "Export diagnostic info",

                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",

                DefaultExt = "txt",

                FileName = $"HostWitness_diagnostic_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt"

            };

            if (dialog.ShowDialog() != true)

                return;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var settingsPath = Path.Combine(appData, "HostWitness", "settings.json");

            var logDir = Path.Combine(appData, "HostWitness", "logs");

            var startupLogPath = Path.Combine(logDir, "startup.log");

            var sb = new StringBuilder();

            sb.AppendLine("HostWitness Diagnostic Info");

            sb.AppendLine($"Generated: {DateTime.UtcNow:O} (UTC)");

            sb.AppendLine();

            sb.AppendLine($"Version: {ToolVersionProvider.GetCurrentVersion(typeof(MainWindow))}");

            sb.AppendLine($"Machine: {Environment.MachineName}");

            sb.AppendLine($"Settings: {settingsPath}");

            sb.AppendLine($"Log folder: {logDir}");

            if (_index is InMemoryActivityIndex memIndex)

            {

                sb.AppendLine($"Index events: {memIndex.EventCount}");

                sb.AppendLine($"Index evicted: {memIndex.EvictedEvents}");

            }

            if (_snapshotPath != null)

                sb.AppendLine($"Snapshot loaded: {_snapshotPath}");

            sb.AppendLine($"UI backpressure drops: {Interlocked.Read(ref _uiDroppedEvents)}");

            sb.AppendLine($"Registry mode: {(_registryProvider == null ? "offline_only" : "live_non_forensic")}");

            var etwProvider = _providers.OfType<IEtwThrottleStatsProvider>().FirstOrDefault();

            if (etwProvider != null)

            {

                var etwStats = etwProvider.GetEtwThrottleStats();

                if (etwStats.TotalDrops.Count > 0)

                {

                    sb.AppendLine("ETW throttle (total drops):");

                    foreach (var kv in etwStats.TotalDrops.OrderBy(k => k.Key))

                        sb.AppendLine($"  {kv.Key}: {kv.Value}");

                }

            }

            sb.AppendLine();

            sb.AppendLine("--- Known limitations (see docs/LIMITATIONS.md) ---");

            sb.AppendLine("- File locking: VSS snapshot used where possible; fallback to live path. Admin + VSS service required.");

            sb.AppendLine("- Rootkit/API hooking: Results may be affected if raw registry/MFT parsing is not used; live API can be hooked.");

            sb.AppendLine("- ETW throttling: High-frequency events may be dropped; drop counts reported above and in status bar.");

            sb.AppendLine();

            if (File.Exists(startupLogPath))

            {

                sb.AppendLine("--- Last 30 lines of startup.log ---");

                var lines = File.ReadAllLines(startupLogPath);

                foreach (var line in lines.TakeLast(30))

                    sb.AppendLine(line);

            }

            File.WriteAllText(dialog.FileName, sb.ToString());

            MessageBox.Show($"Diagnostic info saved to:\n{dialog.FileName}", "Export diagnostic info", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        catch (Exception ex)

        {

            MessageBox.Show($"Failed to export diagnostic info: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

        }

    }



    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)

    {

        MessageBox.Show(

            $"HostWitness - Live Forensics & Activity Correlation\n\nVersion {ToolVersionProvider.GetCurrentVersion(typeof(MainWindow))}\n\nA single-host live forensics and activity correlation tool.\n\nCopyright (c) 2026 nine-security Inc. All rights reserved.",

            "About HostWitness",

            MessageBoxButton.OK,

            MessageBoxImage.Information);

    }



    private void UpdatePlayPauseButtonIcon(Button button, bool isPaused)

    {

        if (button?.Content is System.Windows.Shapes.Path path)

        {

            if (isPaused)

            {

                // Show play icon (triangle) when paused

                path.Data = Geometry.Parse("M8,4 L8,20 L21,12 Z");

                path.Fill = Brushes.Green;

                path.Stroke = null;

                path.StrokeThickness = 0;

                button.ToolTip = "Play";

            }

            else

            {

                // Show pause icon (two vertical lines) when playing

                path.Data = Geometry.Parse("M7,4 L7,20 M19,4 L19,20");

                path.Fill = null;

                path.Stroke = Brushes.Red;

                path.StrokeThickness = 4;

                path.StrokeStartLineCap = PenLineCap.Round;

                path.StrokeEndLineCap = PenLineCap.Round;

                button.ToolTip = "Pause";

            }

        }

    }



    private void TrimIndexQueuesIfNeeded(DateTime nowUtc)

    {

        if (_index is not InMemoryActivityIndex memoryIndex)

            return;

        if (memoryIndex.EvictedEvents == 0)

            return;

        // On first eviction, trim immediately to reclaim memory (OOM mitigation)

        if (_lastIndexTrimUtc == DateTime.MinValue)

        {

            _lastIndexTrimUtc = nowUtc;

            memoryIndex.TrimAllQueues();

            return;

        }

        if ((nowUtc - _lastIndexTrimUtc).TotalMinutes < 2)

            return;

        _lastIndexTrimUtc = nowUtc;

        memoryIndex.TrimAllQueues();

    }



    private void ReportIndexEvictions(DateTime nowUtc)

    {

        if (_index is not InMemoryActivityIndex memoryIndex)

            return;



        var evicted = memoryIndex.EvictedEvents;

        if (evicted <= _lastEvictedReported)

            return;



        if ((nowUtc - _lastEvictedReportUtc).TotalSeconds < 5)

            return;



        var delta = evicted - _lastEvictedReported;

        _lastEvictedReported = evicted;

        _lastEvictedReportUtc = nowUtc;



        var evt = new ActivityEvent

        {

            Category = "System",

            Action = "Query",

            Timestamp = nowUtc,

            Summary = $"Index evicted {delta} events (total {evicted})",

            Evidence = new List<EvidenceRef>

            {

                new EvidenceRef("Index", "InMemoryActivityIndex", null, nowUtc)

            },

            Fields = new Dictionary<string, object>

            {

                ["EvictedDelta"] = delta,

                ["EvictedTotal"] = evicted,

                ["EventCount"] = memoryIndex.EventCount,

                ["MaxEvents"] = memoryIndex.MaxEventCapacity

            },

            Confidence = "Low"

        };



        OnEventProduced(this, evt);

    }



    private void UpdateIndexStatus(DateTime nowUtc)

    {

        if (IndexStatusTextBlock == null || _index is not InMemoryActivityIndex memoryIndex)

            return;

        var maxCap = memoryIndex.MaxEventCapacity;

        if (maxCap <= 0)

            return;

        var count = memoryIndex.EventCount;

        if (count >= (long)(0.9 * maxCap))

        {

            IndexStatusTextBlock.Visibility = Visibility.Visible;

            IndexStatusTextBlock.Text = $"Index ~{ (int)(100.0 * count / maxCap)}% full (eviction active). Consider raising Max events in Settings or exporting.";

        }

        else

        {

            IndexStatusTextBlock.Visibility = Visibility.Collapsed;

            IndexStatusTextBlock.Text = "";

        }

    }



    private void UpdateCollectionWarnings()

    {

        if (CollectionWarningsTextBlock == null)

            return;

        var messages = CollectionWarnings.SnapshotAndClear();

        if (messages.Length == 0)

        {

            CollectionWarningsTextBlock.Visibility = Visibility.Collapsed;

            CollectionWarningsTextBlock.Text = "";

            return;

        }

        CollectionWarningsTextBlock.Visibility = Visibility.Visible;

        var text = "File access warnings: " + string.Join("; ", messages.Take(5));

        if (messages.Length > 5)

            text += $" (+{messages.Length - 5} more)";

        CollectionWarningsTextBlock.Text = text;

        CollectionWarningsTextBlock.ToolTip = "Some artifacts could not be read (locked or access denied). Use Admin + VSS for offline analysis. See LIMITATIONS section 1.";

    }



    private void UpdateProcessCacheStatus(DateTime nowUtc)

    {

        if (CacheStatusTextBlock == null)

            return;



        if ((nowUtc - _lastCacheStatusUtc).TotalSeconds < 5)

            return;



        _lastCacheStatusUtc = nowUtc;



        var stats = _providers

            .OfType<IProcessCreateCacheStatsProvider>()

            .Select(provider => provider.GetProcessCreateCacheStats())

            .ToList();



        if (stats.Count == 0)

        {

            CacheStatusTextBlock.Text = "PID Cache: -";

            return;

        }



        var parts = stats.Select(s =>

            $"{s.ProviderName}: {s.TotalEntries} (P {s.ProvisionalEntries}) / {s.MaxEntries}");

        CacheStatusTextBlock.Text = $"PID Cache: {string.Join(" | ", parts)}";

    }



    private void UpdateEtwThrottleStatus(DateTime nowUtc)

    {

        if (EtwThrottleTextBlock == null)

            return;



        if ((nowUtc - _lastEtwThrottleStatusUtc).TotalSeconds < 5)

            return;



        _lastEtwThrottleStatusUtc = nowUtc;



        var provider = _providers.OfType<IEtwThrottleStatsProvider>().FirstOrDefault();

        if (provider == null)

        {

            EtwThrottleTextBlock.Text = "ETW Throttle: -";

            return;

        }



        var stats = provider.GetEtwThrottleStats();

        if (stats.LastReportedDrops.Count == 0 && stats.TotalDrops.Count == 0)

        {

            EtwThrottleTextBlock.Text = "ETW Throttle: none";

            EtwThrottleTextBlock.ToolTip = null;

            return;

        }



        var parts = stats.LastReportedDrops

            .OrderBy(kvp => kvp.Key)

            .Select(kvp => $"{kvp.Key} {kvp.Value}");

        EtwThrottleTextBlock.Text = stats.LastReportedDrops.Count > 0

            ? $"ETW Throttle (last): {string.Join(", ", parts)}"

            : "ETW Throttle: (drops earlier)";

        if (stats.TotalDrops.Count > 0)

        {

            var totalParts = stats.TotalDrops.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}");

            EtwThrottleTextBlock.ToolTip = "Total drops: " + string.Join(", ", totalParts)

                + ". High-frequency events may be lost. See LIMITATIONS section 10.";

        }

        else

            EtwThrottleTextBlock.ToolTip = null;

    }



    private void UpdateUiBackpressureStatus(DateTime nowUtc)

    {

        if (UiBackpressureTextBlock == null)

            return;

        if ((nowUtc - _lastUiQueueStatusUtc).TotalSeconds < 2)

            return;

        _lastUiQueueStatusUtc = nowUtc;



        var pending = Math.Max(0, Interlocked.Read(ref _uiEventCount));

        var droppedTotal = Math.Max(0, Interlocked.Read(ref _uiDroppedEvents));

        var droppedDelta = Math.Max(0, droppedTotal - _lastUiDroppedReported);

        _lastUiDroppedReported = droppedTotal;

        var fillPct = (int)(100.0 * Math.Min(pending, MaxUiEvents) / MaxUiEvents);



        UiBackpressureTextBlock.Text =

            $"UI Queue: {pending}/{MaxUiEvents} (~{fillPct}%), dropped total {droppedTotal}" +

            (droppedDelta > 0 ? $" (+{droppedDelta} recently)" : string.Empty);

        UiBackpressureTextBlock.ToolTip =

            "Backpressure protects responsiveness by dropping excess UI-queue events when producer rate exceeds render throughput.";

    }



    private void DrillDownButton_Click(object sender, RoutedEventArgs e)

    {

        // Entity drill-down: navigate from selected entity to related views

        // This is a simplified implementation - full drill-down would show related entities

        

        if (DynamicTabControl?.SelectedItem == TimelineTab)

        {

            // If timeline view is active, try to drill down from selected event

            var timelineView = TimelineViewControl;

            // Drill-down logic would be implemented here

            MessageBox.Show("Drill-down feature: Select an event and navigate to related entities.", 

                "Drill Down", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        else if (DynamicTabControl?.SelectedItem == ProcessTab)

        {

            // If process view is active, show related network/file events

            var processView = ProcessViewControl;

            MessageBox.Show("Drill-down feature: Show related network and file events for selected process.", 

                "Drill Down", MessageBoxButton.OK, MessageBoxImage.Information);

        }

        else if (StaticTabControl?.SelectedItem == NetworkTab)

        {

            // If network view is active, show related process/file events

            var networkView = NetworkViewControl;

            MessageBox.Show("Drill-down feature: Show related process and file events for selected connection.", 

                "Drill Down", MessageBoxButton.OK, MessageBoxImage.Information);

        }

    }

}


