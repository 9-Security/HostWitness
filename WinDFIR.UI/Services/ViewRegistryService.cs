using System.Collections.Generic;
using System.Windows.Controls;
using WinDFIR.UI.Views;

namespace WinDFIR.UI.Services;

/// <summary>
/// Holds view instances and detached-window state for dynamic and static tabs.
/// Decouples MainWindow from direct ownership of view/detach dictionaries so that
/// a future Docking or multi-window implementation can replace this service.
/// See docs\TECH_DEBT.md §2, §4.
/// </summary>
public sealed class ViewRegistryService
{
    private readonly Dictionary<string, UserControl> _dynamicViews = new();
    private readonly Dictionary<string, UserControl> _staticViews = new();
    private readonly Dictionary<string, (DetachedTabWindow Window, UserControl View)> _detachedDynamic = new();
    private readonly Dictionary<string, (DetachedTabWindow Window, UserControl View)> _detachedStatic = new();

    public void RegisterDynamicView(string key, UserControl view)
    {
        _dynamicViews[key] = view;
    }

    public void RegisterStaticView(string key, UserControl view)
    {
        _staticViews[key] = view;
    }

    public UserControl? GetDynamicView(string key)
    {
        return _dynamicViews.TryGetValue(key, out var v) ? v : null;
    }

    public UserControl? GetStaticView(string key)
    {
        return _staticViews.TryGetValue(key, out var v) ? v : null;
    }

    public bool IsDynamicDetached(string key) => _detachedDynamic.ContainsKey(key);
    public bool IsStaticDetached(string key) => _detachedStatic.ContainsKey(key);

    public void SetDetachedDynamic(string key, DetachedTabWindow window, UserControl view)
    {
        _detachedDynamic[key] = (window, view);
    }

    public void SetDetachedStatic(string key, DetachedTabWindow window, UserControl view)
    {
        _detachedStatic[key] = (window, view);
    }

    public bool TryGetDetachedDynamic(string key, out DetachedTabWindow? window, out UserControl? view)
    {
        if (_detachedDynamic.TryGetValue(key, out var pair))
        {
            window = pair.Window;
            view = pair.View;
            return true;
        }
        window = null;
        view = null;
        return false;
    }

    public bool TryGetDetachedStatic(string key, out DetachedTabWindow? window, out UserControl? view)
    {
        if (_detachedStatic.TryGetValue(key, out var pair))
        {
            window = pair.Window;
            view = pair.View;
            return true;
        }
        window = null;
        view = null;
        return false;
    }

    public void RestoreDynamic(string key)
    {
        _detachedDynamic.Remove(key);
    }

    public void RestoreStatic(string key)
    {
        _detachedStatic.Remove(key);
    }

    public IEnumerable<(string Key, DetachedTabWindow Window, UserControl View)> GetDetachedDynamicTabs()
    {
        foreach (var kv in _detachedDynamic)
            yield return (kv.Key, kv.Value.Window, kv.Value.View);
    }

    public IEnumerable<(string Key, DetachedTabWindow Window, UserControl View)> GetDetachedStaticTabs()
    {
        foreach (var kv in _detachedStatic)
            yield return (kv.Key, kv.Value.Window, kv.Value.View);
    }
}
