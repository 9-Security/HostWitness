using WinDFIR.Core.Normalization;
using Xunit;

namespace WinDFIR.Tests;

public class TimeNormalizerTests
{
    [Fact]
    public void FromFileTime_WithValidFileTime_ReturnsUtcDateTime()
    {
        // Arrange
        // FileTime for 2024-01-01 12:00:00 UTC
        var fileTime = (ulong)new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

        // Act
        var dateTime = TimeNormalizer.FromFileTime(fileTime);

        // Assert
        Assert.Equal(DateTimeKind.Utc, dateTime.Kind);
        Assert.Equal(2024, dateTime.Year);
        Assert.Equal(1, dateTime.Month);
        Assert.Equal(1, dateTime.Day);
    }

    [Fact]
    public void FromUnixTimeSeconds_WithValidTimestamp_ReturnsUtcDateTime()
    {
        // Arrange
        // Unix timestamp for 2024-01-01 12:00:00 UTC
        var unixSeconds = 1704110400L;

        // Act
        var dateTime = TimeNormalizer.FromUnixTimeSeconds(unixSeconds);

        // Assert
        Assert.Equal(DateTimeKind.Utc, dateTime.Kind);
        Assert.Equal(2024, dateTime.Year);
        Assert.Equal(1, dateTime.Month);
        Assert.Equal(1, dateTime.Day);
    }

    [Fact]
    public void FromUnixTimeMilliseconds_WithValidTimestamp_ReturnsUtcDateTime()
    {
        // Arrange
        // Unix timestamp in milliseconds for 2024-01-01 12:00:00 UTC
        var unixMilliseconds = 1704110400000L;

        // Act
        var dateTime = TimeNormalizer.FromUnixTimeMilliseconds(unixMilliseconds);

        // Assert
        Assert.Equal(DateTimeKind.Utc, dateTime.Kind);
        Assert.Equal(2024, dateTime.Year);
        Assert.Equal(1, dateTime.Month);
        Assert.Equal(1, dateTime.Day);
    }

    [Fact]
    public void NormalizeToUtc_WithUtcDateTime_ReturnsSame()
    {
        // Arrange
        var utcDateTime = DateTime.UtcNow;

        // Act
        var normalized = TimeNormalizer.NormalizeToUtc(utcDateTime);

        // Assert
        Assert.Equal(utcDateTime, normalized);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
    }

    [Fact]
    public void NormalizeToUtc_WithLocalDateTime_ConvertsToUtc()
    {
        // Arrange
        var localDateTime = DateTime.Now;

        // Act
        var normalized = TimeNormalizer.NormalizeToUtc(localDateTime);

        // Assert
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
    }

    [Fact]
    public void NormalizeToUtc_WithUnspecifiedDateTime_AssumesUtc()
    {
        // Arrange
        var unspecifiedDateTime = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);

        // Act
        var normalized = TimeNormalizer.NormalizeToUtc(unspecifiedDateTime);

        // Assert
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
    }
}
