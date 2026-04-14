using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace WinDFIR.Tests;

public class HandoffScriptSmokeTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 24 && dir != null; i++)
        {
            var script = Path.Combine(dir.FullName, "scripts", "RunCollectAndCopy.ps1");
            if (File.Exists(script))
                return dir.FullName;
            var sln = Path.Combine(dir.FullName, "WinDFIR.sln");
            if (File.Exists(sln))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (WinDFIR.sln or scripts/RunCollectAndCopy.ps1).");
    }

    [Fact]
    public async Task RunCollectAndCopy_MissingAgentPath_WritesStatusJson_Exit1()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "scripts", "RunCollectAndCopy.ps1");
        Assert.True(File.Exists(script));

        var temp = Path.Combine(Path.GetTempPath(), "HostWitness_RunCollectAgent_" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(temp, "out");
        var copyDest = Path.Combine(temp, "dest");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(copyDest);
        var fakeAgent = Path.Combine(temp, "MissingHostWitness.Agent.exe");

        try
        {
            // Do not pass -VerifyCopy; default applies but we fail before copy when AgentPath is missing.
            var args =
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " +
                "-AgentPath \"" + fakeAgent + "\" -OutputDir \"" + outDir + "\" -CollectSeconds 1 " +
                "-CopyToPath \"" + copyDest + "\"";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            Assert.NotNull(p);
            await p.WaitForExitAsync();
            Assert.Equal(1, p.ExitCode);

            var statusFile = Path.Combine(outDir, "collect-status-latest.json");
            Assert.True(File.Exists(statusFile));
            var json = await File.ReadAllTextAsync(statusFile);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("agent_not_found", doc.RootElement.GetProperty("stage").GetString());
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ScheduledCollect_PostCopyScript_UsesSingleQuoteDoublingForEmbeds()
    {
        var path = Path.Combine(FindRepoRoot(), "scripts", "ScheduledCollect.ps1");
        var text = File.ReadAllText(path);
        Assert.Contains("$embOut = $OutputDir.Replace(\"'\", \"''\")", text);
        Assert.Contains("$embDest = $CopyToPath.Replace(\"'\", \"''\")", text);
        Assert.Contains("$embKey = $StatusHmacKey.Replace(\"'\", \"''\")", text);
        Assert.Contains("`$out = '$embOut'", text);
        Assert.Contains("`$dest = '$embDest'", text);
        Assert.Contains("`$statusHmacKey = '$embKey'", text);
    }
}
