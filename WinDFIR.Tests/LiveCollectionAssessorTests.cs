using WinDFIR.Core.Index;
using Xunit;

namespace WinDFIR.Tests;

public class LiveCollectionAssessorTests
{
    [Fact]
    public void Assess_NoLossWellUnderCap_IsGreenAndSilent()
    {
        var s = LiveCollectionAssessor.Assess(indexEventCount: 1000, indexMaxCapacity: 200_000, evictedEvents: 0, uiRenderDropped: 0);

        Assert.Equal(CollectionCompletenessLevel.Green, s.Level);
        Assert.False(s.ShouldWarn);
        Assert.False(s.IsLosingData);
    }

    [Fact]
    public void Assess_Eviction_IsRedAndFlagsPermanentLoss()
    {
        var s = LiveCollectionAssessor.Assess(indexEventCount: 200_000, indexMaxCapacity: 200_000, evictedEvents: 4231, uiRenderDropped: 0);

        Assert.Equal(CollectionCompletenessLevel.Red, s.Level);
        Assert.True(s.ShouldWarn);
        Assert.True(s.IsLosingData);
        Assert.Contains("4,231", s.Headline);
        Assert.Contains("export", s.Detail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assess_EvictionTakesPriorityOverRenderDrops()
    {
        // Both present -> the permanent index loss (Red) wins over cosmetic render drops (Amber).
        var s = LiveCollectionAssessor.Assess(indexEventCount: 200_000, indexMaxCapacity: 200_000, evictedEvents: 10, uiRenderDropped: 999);

        Assert.Equal(CollectionCompletenessLevel.Red, s.Level);
        Assert.True(s.IsLosingData);
    }

    [Fact]
    public void Assess_NearCapacityNoEvictionYet_IsAmber()
    {
        var s = LiveCollectionAssessor.Assess(indexEventCount: 185_000, indexMaxCapacity: 200_000, evictedEvents: 0, uiRenderDropped: 0);

        Assert.Equal(CollectionCompletenessLevel.Amber, s.Level);
        Assert.False(s.IsLosingData); // not lost yet — imminent, not actual
        Assert.Contains("imminent", s.Headline, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assess_JustBelowNearCapacity_IsGreen()
    {
        // 89% < 90% threshold
        var s = LiveCollectionAssessor.Assess(indexEventCount: 178_000, indexMaxCapacity: 200_000, evictedEvents: 0, uiRenderDropped: 0);

        Assert.Equal(CollectionCompletenessLevel.Green, s.Level);
    }

    [Fact]
    public void Assess_RenderDropsOnly_IsAmber_AndPersistedUnaffected()
    {
        var s = LiveCollectionAssessor.Assess(indexEventCount: 1000, indexMaxCapacity: 200_000, evictedEvents: 0, uiRenderDropped: 50);

        Assert.Equal(CollectionCompletenessLevel.Amber, s.Level);
        Assert.False(s.IsLosingData);
        Assert.Contains("UNAFFECTED", s.Detail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Assess_UnboundedIndex_NeverWarnsOnCapacity()
    {
        // maxCapacity 0 = unbounded; even a huge count must not trigger the near-capacity branch.
        var s = LiveCollectionAssessor.Assess(indexEventCount: 5_000_000, indexMaxCapacity: 0, evictedEvents: 0, uiRenderDropped: 0);

        Assert.Equal(CollectionCompletenessLevel.Green, s.Level);
    }
}
