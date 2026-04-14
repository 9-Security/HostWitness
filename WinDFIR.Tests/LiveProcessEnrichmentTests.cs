using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Providers;
using WinDFIR.UI.ViewModels;
using Xunit;

namespace WinDFIR.Tests;

public class LiveProcessEnrichmentTests
{
    [Fact]
    public void TokenHelper_CurrentProcess_AddsStableFieldsWithoutThrowing()
    {
        var fields = new Dictionary<string, object>
        {
            ["UserName"] = "Unknown",
            ["Integrity"] = "Unknown"
        };

        var ex = Record.Exception(() =>
            LiveProcessTokenHelper.TryAddTokenFields(Environment.ProcessId, fields));

        Assert.Null(ex);
        Assert.True(fields.ContainsKey("OwnerSid"));
        Assert.False(string.IsNullOrWhiteSpace(fields["OwnerSid"]?.ToString()));
        Assert.True(fields.ContainsKey("SessionId"));
        Assert.NotEqual("Unknown", fields["Integrity"]?.ToString());
        Assert.False(string.IsNullOrWhiteSpace(fields["UserName"]?.ToString()));
    }

    [Fact]
    public void TokenHelper_InvalidProcessId_DoesNotThrow_AndLeavesOptionalKeysAbsent()
    {
        var fields = new Dictionary<string, object>
        {
            ["UserName"] = "Unknown",
            ["Integrity"] = "Unknown"
        };

        var ex = Record.Exception(() => LiveProcessTokenHelper.TryAddTokenFields(int.MaxValue, fields));

        Assert.Null(ex);
        Assert.False(fields.ContainsKey("OwnerSid"));
        Assert.Equal("Unknown", fields["Integrity"]);
    }

    [Fact]
    public void AuthenticodeHelper_SystemNotepad_IfPresent_DoesNotThrow()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var notepad = Path.Combine(windir, "notepad.exe");
        if (!File.Exists(notepad))
            return;

        var fields = new Dictionary<string, object>();
        var ex = Record.Exception(() =>
            LiveProcessAuthenticodeHelper.TryAddAuthenticodeFields(notepad, fields));

        Assert.Null(ex);
        Assert.True(fields.ContainsKey("AuthenticodeStatus"));
    }

    [Fact]
    public void AuthenticodeHelper_MissingPath_IsNoOp()
    {
        var fields = new Dictionary<string, object>();
        LiveProcessAuthenticodeHelper.TryAddAuthenticodeFields(null, fields);
        LiveProcessAuthenticodeHelper.TryAddAuthenticodeFields(@"Z:\no\such\file.exe", fields);
        Assert.Empty(fields);
    }

    [Fact]
    public void ProviderSeam_BaseFields_Emitted_WhenEnrichmentSkipped()
    {
        var fields = LiveProcessProvider.BuildLiveProcessStartFieldsForTest(
            123,
            "testproc",
            "cmd /c",
            @"C:\temp\a.exe",
            4,
            @"C:\Windows\parent.exe",
            4096,
            "Co",
            "deadbeef",
            tokenEnrich: (_, _) => { },
            authenticodeEnrich: (_, _) => { });

        Assert.Equal("testproc", fields["ProcessName"]);
        Assert.Equal(123, fields["ProcessId"]);
        Assert.Equal(4, fields["ParentProcessId"]);
        Assert.Equal(@"C:\Windows\parent.exe", fields["ParentImagePath"]);
        Assert.False(fields.ContainsKey("OwnerSid"));
        Assert.False(fields.ContainsKey("AuthenticodeStatus"));
    }

    [Fact]
    public void ProviderSeam_StartEvent_IncludesEnrichmentForCurrentProcess_WhenHooksDefault()
    {
        var ts = DateTime.UtcNow;
        var evt = LiveProcessProvider.BuildLiveProcessStartActivityEventForTest(
            bootId: 1,
            processId: (uint)Environment.ProcessId,
            processName: "Current",
            createTimeUtc: ts,
            commandLine: "",
            imagePath: null,
            parentPid: 0,
            parentImagePath: "",
            workingSet: 100,
            company: "",
            hash: "",
            tokenEnrich: null,
            authenticodeEnrich: null);

        Assert.Equal("Process", evt.Category);
        Assert.Equal("Start", evt.Action);
        Assert.NotNull(evt.SubjectProcess);
        Assert.NotEmpty(evt.Evidence);
        Assert.True(evt.Fields.ContainsKey("OwnerSid"));
        Assert.True(evt.Fields.ContainsKey("SessionId"));
    }

    [Fact]
    public void ProviderSeam_Authenticode_DegradesWhenImagePathMissing()
    {
        var fields = LiveProcessProvider.BuildLiveProcessStartFieldsForTest(
            Environment.ProcessId,
            "x",
            "",
            "",
            0,
            "",
            0,
            "",
            "",
            tokenEnrich: (_, _) => { },
            authenticodeEnrich: null);

        Assert.False(fields.ContainsKey("AuthenticodeStatus"));
    }

    [Fact]
    public void ProcessColumnSettings_PreVersion2_AppliesPhase4Defaults()
    {
        const string legacyJson = """{"ShowProcessName":true,"ShowPid":true,"ColumnSettingsVersion":0}""";
        var settings = JsonSerializer.Deserialize<ProcessViewColumnSettings>(legacyJson);
        Assert.NotNull(settings);

        ProcessViewModel.GetPhase4ColumnVisibility(settings, out var ownerSid, out var session, out var parentImg,
            out var auth);

        Assert.False(ownerSid);
        Assert.True(session);
        Assert.True(parentImg);
        Assert.True(auth);
    }

    [Fact]
    public void ProcessColumnSettings_Version2_UsesPersistedPhase4Flags()
    {
        const string json =
            """{"ColumnSettingsVersion":2,"ShowOwnerSid":true,"ShowSessionId":false,"ShowParentImagePath":false,"ShowAuthenticode":false,"ShowProcessName":true}""";
        var settings = JsonSerializer.Deserialize<ProcessViewColumnSettings>(json);
        Assert.NotNull(settings);

        ProcessViewModel.GetPhase4ColumnVisibility(settings, out var ownerSid, out var session, out var parentImg,
            out var auth);

        Assert.True(ownerSid);
        Assert.False(session);
        Assert.False(parentImg);
        Assert.False(auth);
    }
}
