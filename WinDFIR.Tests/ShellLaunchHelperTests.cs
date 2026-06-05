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

    [Theory]
    [InlineData(@"C:\App\docs\help.html", @"C:\App", true)]
    [InlineData(@"C:\App", @"C:\App", false)]               // base itself is not "within"
    [InlineData(@"C:\AppEvil\x.html", @"C:\App", false)]     // sibling-prefix must not escape
    [InlineData(@"C:\App\sub\file.txt", @"C:\App\", true)]   // base already has trailing separator
    [InlineData(@"C:\Other\x", @"C:\App", false)]
    public void IsWithinBaseDirectory_BlocksSiblingPrefixEscape(string fullPath, string baseDir, bool expected)
    {
        Assert.Equal(expected, ShellLaunchHelper.IsWithinBaseDirectory(fullPath, baseDir));
    }
}
