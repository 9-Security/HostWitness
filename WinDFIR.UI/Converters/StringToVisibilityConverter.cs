using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinDFIR.UI.Converters;

/// <summary>
/// Converts string to Visibility: non-empty => Visible, empty => Collapsed.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
