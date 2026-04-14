using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
namespace WinDFIR.UI.Converters;

/// <summary>
/// Multi-value converter: (DateTime timestamp, bool useUtc) -> formatted string for display.
/// </summary>
public class TimeZoneDisplayConverter : IMultiValueConverter
{
    private const string Format = "yyyy-MM-dd HH:mm:ss.fff";

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
            return string.Empty;

        if (values[0] is not DateTime dt)
            return string.Empty;

        var useUtc = values[1] is bool b && b;
        var display = useUtc ? dt : dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        return display.ToString(Format, culture ?? CultureInfo.CurrentCulture);
    }

    /// <summary>OneWay only; return no-op values for any accidental reverse binding.</summary>
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
