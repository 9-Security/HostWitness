using System;
using System.Windows;
using System.Windows.Controls;

namespace WinDFIR.UI.Views;

public partial class DetachedTabWindow : Window
{
    private UserControl? _detachedContent;
    private ToolBar? _toolBar;

    public DetachedTabWindow(string title, UserControl content)
    {
        InitializeComponent();
        Title = title;
        _detachedContent = content;
        ContentHost.Content = content;
    }

    public bool SuppressRestore { get; set; }

    public event EventHandler<UserControl>? RestoreRequested;

    public UserControl? DetachContent()
    {
        var content = _detachedContent;
        _detachedContent = null;
        ContentHost.Content = null;
        return content;
    }

    public void SetToolBar(ToolBar toolBar)
    {
        if (_toolBar != null)
        {
            ToolBarHost.ToolBars.Remove(_toolBar);
        }

        _toolBar = toolBar;
        ToolBarHost.ToolBars.Add(toolBar);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (SuppressRestore)
            return;

        var content = DetachContent();
        if (content != null)
        {
            RestoreRequested?.Invoke(this, content);
        }
    }
}
