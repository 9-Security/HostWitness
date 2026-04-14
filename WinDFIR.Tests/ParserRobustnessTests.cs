using System.IO;
using WinDFIR.Core.Mft;
using WinDFIR.Core.Normalization;
using WinDFIR.Core.Entities;
using Xunit;

namespace WinDFIR.Tests;

/// <summary>
/// Parser robustness: empty/invalid input should not throw; normalizers handle null/empty.
/// </summary>
public class ParserRobustnessTests
{
    [Fact]
    public void MftParser_EmptyStream_YieldsNothing()
    {
        using var stream = new MemoryStream();
        var entries = MftParser.Parse(stream, 1024).ToList();
        Assert.Empty(entries);
    }

    [Fact]
    public void MftParser_StreamWithNoFileSignature_YieldsNothing()
    {
        var buffer = new byte[1024];
        for (int i = 0; i < 4; i++)
            buffer[i] = (byte)'X';
        using var stream = new MemoryStream(buffer);
        var entries = MftParser.Parse(stream, 1024).ToList();
        Assert.Empty(entries);
    }

    [Fact]
    public void ActivityEventNormalizer_NullOrEmptyAction_ReturnsQuery()
    {
        var evt = new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "Test",
            Action = "open",
            Evidence = new List<EvidenceRef>()
        };
        var normalized = ActivityEventNormalizer.Normalize(evt);
        Assert.Equal("Open", normalized.Action);

        var evtEmptyAction = new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "Test",
            Action = "",
            Evidence = new List<EvidenceRef>()
        };
        var normEmptyAction = ActivityEventNormalizer.Normalize(evtEmptyAction);
        Assert.Equal("Query", normEmptyAction.Action);

        var evtWhiteSpace = new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "Test",
            Action = "   ",
            Evidence = new List<EvidenceRef>()
        };
        var normWs = ActivityEventNormalizer.Normalize(evtWhiteSpace);
        Assert.Equal("Query", normWs.Action);
    }

    [Fact]
    public void ActivityEventNormalizer_NormalizeAction_NullOrWhiteSpace_ReturnsQuery()
    {
        Assert.Equal("Query", ActivityEventNormalizer.NormalizeAction(null!));
        Assert.Equal("Query", ActivityEventNormalizer.NormalizeAction(""));
        Assert.Equal("Query", ActivityEventNormalizer.NormalizeAction("   "));
    }
}
