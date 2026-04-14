using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

public class SnapshotImporterKeyRoundTripTests
{
    [Fact]
    public void ParseEventFromJson_RoundTripsProcessKey_WithIsoCreateTimeContainingColons()
    {
        var key = KeyGenerator.GenerateProcessKey(0xDEADBEEFCAFEBABE, 4242,
            new DateTime(2024, 7, 15, 14, 30, 45, 123, DateTimeKind.Utc));
        var json = $$"""
            {"timestamp":"2024-07-15T14:30:45.1230000Z","category":"c","action":"a","subjectProcess":"{{key}}","summary":null,"fields":{},"evidence":[],"confidence":"Medium"}
            """;
        var ev = SnapshotImporter.ParseEventFromJson(json);
        Assert.NotNull(ev);
        Assert.Equal(key, ev.SubjectProcess);
    }

    [Fact]
    public void ParseEventFromJson_RoundTripsNetworkFlowKey_WithIpv6StyleEndpoints()
    {
        var time = new DateTime(2024, 3, 20, 10, 0, 0, DateTimeKind.Utc);
        var key = KeyGenerator.GenerateNetworkFlowKey("TCP", "[::1]:8080", "[2001:db8::2]:443", 9999, time);
        var json = $$"""
            {"timestamp":"2024-03-20T10:00:00.0000000Z","category":"c","action":"a","objectNetworkFlow":"{{key}}","summary":null,"fields":{},"evidence":[],"confidence":"Medium"}
            """;
        var ev = SnapshotImporter.ParseEventFromJson(json);
        Assert.NotNull(ev);
        Assert.Equal(key, ev.ObjectNetworkFlow);
    }

    [Fact]
    public void ParseEventFromJson_RoundTripsNetworkFlowKey_WithNullProcessId()
    {
        var time = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var key = new NetworkFlowKey("TCP", "192.168.0.1:80", "10.0.0.5:443", null, time);
        var json = $$"""
            {"timestamp":"2024-01-01T00:00:00.0000000Z","category":"c","action":"a","objectNetworkFlow":"{{key}}","summary":null,"fields":{},"evidence":[],"confidence":"Medium"}
            """;
        var ev = SnapshotImporter.ParseEventFromJson(json);
        Assert.NotNull(ev);
        Assert.Equal(key, ev.ObjectNetworkFlow);
    }
}
