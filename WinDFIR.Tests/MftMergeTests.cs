using System.Collections.Generic;
using System.Linq;
using WinDFIR.Core.Mft;
using WinDFIR.UI.ViewModels;
using Xunit;

namespace WinDFIR.Tests;

public class MftMergeTests
{
    private static IReadOnlyList<MftEntry> Source(string label, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new MftEntry { RecordIndex = i, Source = label, FileName = $"f{i}", FullPath = $"{label}\\f{i}" })
            .ToList();

    [Fact]
    public void BuildMergedEntries_ConcatenatesAllSources_SourceGroupedOrder()
    {
        var a = Source("C:", 3);
        var b = Source("D:", 2);

        var result = MftViewModel.BuildMergedEntries(new[] { a, b }, cap: 0);

        Assert.Equal(2, result.SourceCount);
        Assert.False(result.Truncated);
        Assert.Equal(5, result.Entries.Count);
        // Source-grouped: all of C: then all of D:.
        Assert.Equal(new[] { "C:", "C:", "C:", "D:", "D:" }, result.Entries.Select(e => e.Source).ToArray());
    }

    [Fact]
    public void BuildMergedEntries_HonoursCap_AndFlagsTruncation()
    {
        var a = Source("C:", 4);
        var b = Source("D:", 4);

        var result = MftViewModel.BuildMergedEntries(new[] { a, b }, cap: 6);

        Assert.True(result.Truncated);
        Assert.Equal(6, result.Entries.Count);
        Assert.Equal(2, result.SourceCount); // both sources counted even though the second was cut short
    }

    [Fact]
    public void BuildMergedEntries_CapZeroIsUnbounded()
    {
        var a = Source("C:", 1000);
        var result = MftViewModel.BuildMergedEntries(new[] { a }, cap: 0);

        Assert.False(result.Truncated);
        Assert.Equal(1000, result.Entries.Count);
    }

    [Fact]
    public void BuildMergedEntries_PreservesEntryIdentity_AcrossDuplicateRecordIndexes()
    {
        // Two volumes can have the same RecordIndex; the merged set keeps both, disambiguated by Source.
        var a = Source("C:", 2);
        var b = Source("D:", 2);

        var result = MftViewModel.BuildMergedEntries(new[] { a, b }, cap: 0);

        var recordZero = result.Entries.Where(e => e.RecordIndex == 0).ToList();
        Assert.Equal(2, recordZero.Count);
        Assert.Contains(recordZero, e => e.Source == "C:");
        Assert.Contains(recordZero, e => e.Source == "D:");
    }

    [Fact]
    public void BuildMergedEntries_IgnoresNullSources()
    {
        var a = Source("C:", 2);
        var result = MftViewModel.BuildMergedEntries(new IReadOnlyList<MftEntry>[] { a, null! }, cap: 0);

        Assert.Equal(1, result.SourceCount);
        Assert.Equal(2, result.Entries.Count);
    }

    [Fact]
    public void BuildMergedEntries_NoSources_IsEmpty()
    {
        var result = MftViewModel.BuildMergedEntries(System.Array.Empty<IReadOnlyList<MftEntry>>(), cap: 0);

        Assert.Equal(0, result.SourceCount);
        Assert.Empty(result.Entries);
        Assert.False(result.Truncated);
    }
}
