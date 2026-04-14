using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinDFIR.UI.Converters;

/// <summary>
/// Converts MainViewModel.ToolbarViewType (string) to Visibility.
/// Parameter: one view type (e.g. "Timeline") or pipe-separated list (e.g. "Timeline|LiveStream|Process") for XAML compatibility.
/// Visible when current ToolbarViewType equals or is in the parameter list; otherwise Collapsed.
/// </summary>
public sealed class ToolbarViewTypeToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value?.ToString() ?? string.Empty;
        var param = parameter?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(param))
            return Visibility.Collapsed;
        var allowed = param.Split('|');
        for (var i = 0; i < allowed.Length; i++)
        {
            var t = allowed[i].Trim();
            if (string.Equals(current, t, StringComparison.OrdinalIgnoreCase))
                return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
