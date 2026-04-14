using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinDFIR.UI.Converters;

/// <summary>
/// Inverts a boolean to Visibility mapping.
/// True => Collapsed, False => Visible.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
            return visibility != Visibility.Visible;
        return true;
    }
}
