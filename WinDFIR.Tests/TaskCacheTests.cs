using System;
using System.Collections.Generic;
using WinDFIR.Providers;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

public class TaskCacheTests
{
    [Theory]
    [InlineData(@"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{0FB8D2E0-1234-4ABC-9DEF-0123456789AB}", true, "{0FB8D2E0-1234-4ABC-9DEF-0123456789AB}")]
    [InlineData(@"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks", false, "")] // parent key, no GUID leaf
    [InlineData(@"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\Foo", false, "")] // Tree, not Tasks
    [InlineData(@"Microsoft\Windows\CurrentVersion\Run", false, "")]
    [InlineData(@"", false, "")]
    public void IsTaskCacheTaskKey_DetectsPerTaskGuidKeys(string keyPath, bool expected, string expectedGuid)
    {
        var result = TaskCacheParser.IsTaskCacheTaskKey(keyPath, out var guid);
        Assert.Equal(expected, result);
        Assert.Equal(expectedGuid, guid);
    }

    [Fact]
    public void TryDecodeDynamicInfo_ReadsCreationAndLastRunFiletimes()
    {
        var created = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);
        var lastRun = new DateTime(2026, 3, 15, 22, 30, 0, DateTimeKind.Utc);
        var lastSuccess = new DateTime(2026, 3, 15, 22, 31, 0, DateTimeKind.Utc);

        var raw = new byte[0x24]; // 36-byte blob (Win10 1809+ layout with last-successful at 0x1C)
        BitConverter.GetBytes(0x03).CopyTo(raw, 0);                              // version header
        BitConverter.GetBytes(created.ToFileTimeUtc()).CopyTo(raw, 0x04);
        BitConverter.GetBytes(lastRun.ToFileTimeUtc()).CopyTo(raw, 0x0C);
        BitConverter.GetBytes(lastSuccess.ToFileTimeUtc()).CopyTo(raw, 0x1C);

        var ok = TaskCacheParser.TryDecodeDynamicInfo(raw, out var c, out var lr, out var ls);

        Assert.True(ok);
        Assert.Equal(created, c);
        Assert.Equal(lastRun, lr);
        Assert.Equal(lastSuccess, ls);
    }

    [Fact]
    public void TryDecodeDynamicInfo_ShortBlob_OmitsMissingFields()
    {
        var created = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var raw = new byte[0x14]; // only large enough for created (0x04) + lastRun (0x0C), not lastSuccess (0x1C)
        BitConverter.GetBytes(created.ToFileTimeUtc()).CopyTo(raw, 0x04);
        // lastRun bytes left zero -> rejected

        var ok = TaskCacheParser.TryDecodeDynamicInfo(raw, out var c, out var lr, out var ls);

        Assert.True(ok);
        Assert.Equal(created, c);
        Assert.Null(lr);  // zero FILETIME rejected
        Assert.Null(ls);  // offset past end
    }

    [Fact]
    public void TryDecodeDynamicInfo_RejectsNullTooShortAndImplausible()
    {
        Assert.False(TaskCacheParser.TryDecodeDynamicInfo(null, out _, out _, out _));
        Assert.False(TaskCacheParser.TryDecodeDynamicInfo(new byte[] { 1, 2, 3 }, out _, out _, out _));

        var implausible = new byte[0x14];
        BitConverter.GetBytes(1L).CopyTo(implausible, 0x04); // FILETIME=1 -> year 1601, out of bounds
        Assert.False(TaskCacheParser.TryDecodeDynamicInfo(implausible, out _, out _, out _));
    }

    [Fact]
    public void OfflineHive_DecodesTaskCachePath()
    {
        var baseFields = new Dictionary<string, object> { ["QueryName"] = "TaskCache", ["Mode"] = "Offline" };

        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            baseFields,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{ABCDEF01-2345-6789-ABCD-EF0123456789}",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "TaskCache",
            @"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{ABCDEF01-2345-6789-ABCD-EF0123456789}",
            "Path",
            "REG_SZ",
            @"\Microsoft\Windows\Evil\Backdoor",
            null,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.Equal("TaskCache", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal(@"\Microsoft\Windows\Evil\Backdoor", e.Fields["TaskCache_Path"]);
        Assert.Equal("{ABCDEF01-2345-6789-ABCD-EF0123456789}", e.Fields["TaskCache_Guid"]);
        Assert.Contains("TaskCache task", e.Summary ?? "");
    }

    [Fact]
    public void OfflineHive_DecodesTaskCacheDynamicInfo_AndAnchorsAtLastRun()
    {
        var created = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);
        var lastRun = new DateTime(2026, 3, 15, 22, 30, 0, DateTimeKind.Utc);
        var keyLastWrite = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc); // different from both decoded times

        var raw = new byte[0x18];
        BitConverter.GetBytes(created.ToFileTimeUtc()).CopyTo(raw, 0x04);
        BitConverter.GetBytes(lastRun.ToFileTimeUtc()).CopyTo(raw, 0x0C);

        var baseFields = new Dictionary<string, object> { ["QueryName"] = "TaskCache" };

        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            baseFields,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{11112222-3333-4444-5555-666677778888}",
            keyLastWrite,
            "TaskCache",
            @"Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{11112222-3333-4444-5555-666677778888}",
            "DynamicInfo",
            "REG_BINARY",
            "AA BB CC",
            raw,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.Equal("TaskCache", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal(lastRun, e.Timestamp); // anchored at last run, not key last-write
        Assert.Equal(created.ToString("o", System.Globalization.CultureInfo.InvariantCulture), e.Fields["TaskCache_CreatedUtc"]);
        Assert.Equal(lastRun.ToString("o", System.Globalization.CultureInfo.InvariantCulture), e.Fields["TaskCache_LastRunUtc"]);
        Assert.Contains("TaskCache DynamicInfo", e.Summary ?? "");
    }

    [Fact]
    public void OfflineHive_NonTaskCacheValue_NotDecoded()
    {
        var baseFields = new Dictionary<string, object> { ["QueryName"] = "Run" };
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            baseFields,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            DateTime.UtcNow,
            "Run",
            @"Microsoft\Windows\CurrentVersion\Run",
            "Updater",
            "REG_SZ",
            @"C:\Windows\system32\notepad.exe",
            null,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.False(e.Fields.ContainsKey("TaskCache_Guid"));
    }
}
