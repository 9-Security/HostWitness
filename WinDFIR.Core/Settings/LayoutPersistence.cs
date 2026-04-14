using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace WinDFIR.Core.Settings;

/// <summary>
/// Saves and restores main window layout (position, size, selected tab indices).
/// Used for "Save layout" and "Restore layout" on startup.
/// </summary>
public static class LayoutPersistence
{
    public sealed class DetachedTabLayout
    {
        public string Key { get; set; } = string.Empty;
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public sealed class LayoutState
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int SelectedDynamicTabIndex { get; set; } = -1;
        public int SelectedStaticTabIndex { get; set; }
        public List<DetachedTabLayout> DetachedDynamicTabs { get; set; } = new();
        public List<DetachedTabLayout> DetachedStaticTabs { get; set; } = new();
    }

    private static string LayoutPath()
    {
        // Prefer process environment APPDATA when set so unit tests can redirect storage; matches typical Windows shell behavior.
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "HostWitness", "layout.json");
    }

    public static void Save(
        double left,
        double top,
        double width,
        double height,
        int selectedDynamicTabIndex,
        int selectedStaticTabIndex,
        IEnumerable<DetachedTabLayout>? detachedDynamicTabs = null,
        IEnumerable<DetachedTabLayout>? detachedStaticTabs = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(LayoutPath());
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var obj = new LayoutState
            {
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                SelectedDynamicTabIndex = selectedDynamicTabIndex,
                SelectedStaticTabIndex = selectedStaticTabIndex,
                DetachedDynamicTabs = detachedDynamicTabs?.ToList() ?? new List<DetachedTabLayout>(),
                DetachedStaticTabs = detachedStaticTabs?.ToList() ?? new List<DetachedTabLayout>()
            };
            File.WriteAllText(LayoutPath(), JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Returns (Left, Top, Width, Height, DynamicTabIndex, StaticTabIndex) or null if not found/invalid.</summary>
    public static (double Left, double Top, double Width, double Height, int DynamicTabIndex, int StaticTabIndex)? Load()
    {
        var state = LoadState();
        if (state == null)
            return null;
        return (state.Left, state.Top, state.Width, state.Height, state.SelectedDynamicTabIndex, state.SelectedStaticTabIndex);
    }

    /// <summary>Returns full layout state including detached tab windows; null if file missing/invalid.</summary>
    public static LayoutState? LoadState()
    {
        var path = LayoutPath();
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<LayoutState>(json);
            if (state != null)
            {
                state.DetachedDynamicTabs ??= new List<DetachedTabLayout>();
                state.DetachedStaticTabs ??= new List<DetachedTabLayout>();
                return state;
            }

            // Backward-compatible parsing for old layout schema.
            var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var left = r.TryGetProperty("Left", out var l) && l.TryGetDouble(out var lv) ? lv : double.NaN;
            var top = r.TryGetProperty("Top", out var t) && t.TryGetDouble(out var tv) ? tv : double.NaN;
            var width = r.TryGetProperty("Width", out var w) && w.TryGetDouble(out var wv) ? wv : double.NaN;
            var height = r.TryGetProperty("Height", out var h) && h.TryGetDouble(out var hv) ? hv : double.NaN;
            var dyn = r.TryGetProperty("SelectedDynamicTabIndex", out var d) && d.TryGetInt32(out var di) ? di : -1;
            var st = r.TryGetProperty("SelectedStaticTabIndex", out var s) && s.TryGetInt32(out var si) ? si : 0;
            return new LayoutState
            {
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                SelectedDynamicTabIndex = dyn,
                SelectedStaticTabIndex = st,
                DetachedDynamicTabs = new List<DetachedTabLayout>(),
                DetachedStaticTabs = new List<DetachedTabLayout>()
            };
        }
        catch
        {
            return null;
        }
    }
}
