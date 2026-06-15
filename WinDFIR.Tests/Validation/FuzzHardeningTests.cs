using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests.Validation;

/// <summary>
/// Malformed-input hardening: feed every binary/text parser a battery of garbage and assert the project's two
/// core promises — (1) never crash on bad input (graceful), and (2) never fabricate evidence from it
/// (structured-garbage → empty / false, not invented records or timestamps). Pure unit tests, no samples.
/// </summary>
public class FuzzHardeningTests
{
    /// <summary>Deterministic garbage payloads. "Structured" ones (zeros/0xFF/tiny) must yield nothing.</summary>
    private static IEnumerable<(byte[] Bytes, bool Structured)> Payloads()
    {
        yield return (Array.Empty<byte>(), true);
        yield return (new byte[] { 0x01 }, true);
        yield return (new byte[] { 0, 0, 0 }, true);
        yield return (new byte[16], true);
        yield return (new byte[256], true);
        yield return (new byte[4096], true);
        yield return (Filled(64, 0xFF), true);
        yield return (Filled(256, 0xFF), true);
        yield return (Pseudo(64), false);
        yield return (Pseudo(257), false);
        yield return (Pseudo(2048), false);
    }

    [Fact]
    public void JumpListDestList_NeverCrashes_NoFabricationFromGarbage()
    {
        foreach (var (bytes, structured) in Payloads())
        {
            var result = NoThrow(() => JumpListDestListParser.Parse(bytes), nameof(JumpListDestListParser), bytes);
            if (structured)
                Assert.Empty(result);
        }
    }

    [Fact]
    public void AppCompatCache_NeverCrashes_NoFabricationFromGarbage()
    {
        foreach (var (bytes, structured) in Payloads())
        {
            var result = NoThrow(() => AppCompatCacheParser.Parse(bytes), nameof(AppCompatCacheParser), bytes);
            if (structured)
                Assert.Empty(result);
        }
    }

    [Fact]
    public void RecentDocs_NeverCrashes()
    {
        foreach (var (bytes, _) in Payloads())
            NoThrow(() => RecentDocsParser.Parse(bytes), nameof(RecentDocsParser), bytes);
    }

    [Fact]
    public void Lnk_NeverCrashes()
    {
        foreach (var (bytes, _) in Payloads())
            NoThrow(() => LnkParser.Parse(bytes), nameof(LnkParser), bytes);
    }

    [Fact]
    public void Bits_NeverCrashes()
    {
        foreach (var (bytes, _) in Payloads())
        {
            var strings = NoThrow(() => BitsParser.ExtractUtf16Strings(bytes), "Bits.ExtractUtf16Strings", bytes);
            NoThrow(() => BitsParser.BuildRecord("File", Guid.NewGuid(), strings), "Bits.BuildRecord", bytes);
        }
    }

    [Fact]
    public void Wmi_NeverCrashes_NoFabricationFromGarbage()
    {
        foreach (var (bytes, structured) in Payloads())
        {
            var runs = NoThrow(() => WmiPersistenceParser.ExtractAsciiRuns(bytes), "Wmi.ExtractAsciiRuns", bytes);
            var records = NoThrow(() => WmiPersistenceParser.ExtractFromRuns(runs), "Wmi.ExtractFromRuns", bytes);
            if (structured)
                Assert.Empty(records); // no subscription patterns in zeros/0xFF/tiny
        }
    }

    [Fact]
    public void BamDam_NeverCrashes_NoFabricatedTimestamp()
    {
        foreach (var (bytes, structured) in Payloads())
        {
            // path-like value name so the decoder attempts it; garbage must still yield false.
            var ok = NoThrow(() => { BamDamParser.TryDecodeLastExecution(@"\Device\X\a.exe", bytes, out _); return true; },
                nameof(BamDamParser), bytes);
            if (structured)
                Assert.False(BamDamParser.TryDecodeLastExecution(@"\Device\X\a.exe", bytes, out _));
        }
    }

    [Fact]
    public void TaskCacheDynamicInfo_NeverCrashes_NoFabricatedTimestamp()
    {
        foreach (var (bytes, structured) in Payloads())
        {
            NoThrow(() => { TaskCacheParser.TryDecodeDynamicInfo(bytes, out _, out _, out _); return true; },
                nameof(TaskCacheParser), bytes);
            if (structured)
                Assert.False(TaskCacheParser.TryDecodeDynamicInfo(bytes, out _, out _, out _));
        }
    }

    [Fact]
    public void UserAssist_NeverCrashes_NoFabricationFromGarbage()
    {
        foreach (var (bytes, structured) in Payloads())
        {
            NoThrow(() => { UserAssistParser.TryDecode("SomeValue", bytes, out _); return true; },
                nameof(UserAssistParser), bytes);
            if (structured)
                Assert.False(UserAssistParser.TryDecode("SomeValue", bytes, out _));
        }
    }

    [Fact]
    public void Prefetch_NeverCrashes_NoFabricationFromGarbage()
    {
        foreach (var (bytes, structured) in Payloads())
        {
            var tmp = Path.Combine(Path.GetTempPath(), "hw_fuzz_pf_" + Guid.NewGuid().ToString("N") + ".pf");
            try
            {
                File.WriteAllBytes(tmp, bytes);
                var record = NoThrow(() => PrefetchParser.Parse(tmp), nameof(PrefetchParser), bytes);
                if (structured)
                    Assert.Null(record); // zeros/0xFF/tiny have no valid prefetch header -> no record
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    [Fact]
    public void PowerShellHistory_NeverCrashes()
    {
        var inputs = new[] { null, "", "   ", "\0\0\0", "￾￿", new string('A', 100000) };
        foreach (var s in inputs)
        {
            NoThrow(() => PowerShellHistoryParser.ParseHistory(s), nameof(PowerShellHistoryParser), Array.Empty<byte>());
            NoThrow(() => PowerShellHistoryParser.DetectSuspiciousKeywords(s ?? string.Empty), "PSHistory.Detect", Array.Empty<byte>());
        }
    }

    // --- helpers ---

    private static T NoThrow<T>(Func<T> action, string parser, byte[] bytes)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Assert.Fail($"{parser} threw on malformed input ({bytes.Length} bytes): {ex.GetType().Name}: {ex.Message}");
            throw; // unreachable
        }
    }

    private static byte[] Filled(int n, byte v)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = v;
        return b;
    }

    private static byte[] Pseudo(int n)
    {
        var b = new byte[n];
        new Random(0xC0FFEE).NextBytes(b);
        return b;
    }
}
