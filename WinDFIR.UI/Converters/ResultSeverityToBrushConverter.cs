using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WinDFIR.UI.Converters;

public class ResultSeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return Brushes.Transparent;

        var upper = text.ToUpperInvariant();
        if (upper.Contains("ACCESS DENIED"))
            return new SolidColorBrush(Color.FromRgb(255, 220, 220));

        if (upper.Contains("NAME NOT FOUND") || upper.Contains("BUFFER OVERFLOW") || upper.Contains("NO SUCH FILE"))
            return new SolidColorBrush(Color.FromRgb(255, 242, 204));

        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
