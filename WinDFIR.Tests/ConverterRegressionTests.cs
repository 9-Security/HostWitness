using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using WinDFIR.UI.Converters;
using Xunit;

namespace WinDFIR.Tests;

public class ConverterRegressionTests
{
    [Fact]
    public void BoolToInUseLabelConverter_ConvertBack_ReturnsDoNothing()
    {
        var converter = new BoolToInUseLabelConverter();

        var result = converter.ConvertBack("In use", typeof(bool), null!, CultureInfo.InvariantCulture);

        Assert.Same(Binding.DoNothing, result);
    }

    [Fact]
    public void StringToVisibilityConverter_ConvertBack_ReturnsDoNothing()
    {
        var converter = new StringToVisibilityConverter();

        var result = converter.ConvertBack(Visibility.Visible, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Same(Binding.DoNothing, result);
    }

    [Fact]
    public void ToolbarViewTypeToVisibilityConverter_ConvertBack_ReturnsDoNothing()
    {
        var converter = new ToolbarViewTypeToVisibilityConverter();

        var result = converter.ConvertBack(Visibility.Visible, typeof(string), "Timeline", CultureInfo.InvariantCulture);

        Assert.Same(Binding.DoNothing, result);
    }

    [Fact]
    public void TimeZoneDisplayConverter_ConvertBack_ReturnsDoNothingForEachTarget()
    {
        var converter = new TimeZoneDisplayConverter();

        var result = converter.ConvertBack("2026-03-19 13:20:00.000", new[] { typeof(DateTime), typeof(bool) }, null!, CultureInfo.InvariantCulture);

        Assert.Equal(2, result.Length);
        Assert.All(result, item => Assert.Same(Binding.DoNothing, item));
    }
}