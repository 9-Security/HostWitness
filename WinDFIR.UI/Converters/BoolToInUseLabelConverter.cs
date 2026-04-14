using System;
using System.Globalization;
using System.Windows.Data;

namespace WinDFIR.UI.Converters;

/// <summary>
/// Converts IsInUse (true/false) to "In use" / "Deleted" for MFT list display.
/// </summary>
public class BoolToInUseLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "In use" : "Deleted";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
