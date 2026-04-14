using System;
using WinDFIR.UI.Services;
using Xunit;

namespace WinDFIR.Tests;

public class ShellLaunchHelperTests
{
    [Theory]
    [InlineData("https://example.com/path", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("HTTP://example.com/", true)]
    [InlineData("mailto:test@example.com", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("ftp://example.com/", false)]
    public void IsAllowedHttpOrHttps_RespectsScheme(string uriString, bool expected)
    {
        var uri = new Uri(uriString, UriKind.Absolute);
        Assert.Equal(expected, ShellLaunchHelper.IsAllowedHttpOrHttps(uri));
    }
}
