using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Normalization;
using Xunit;

namespace WinDFIR.Tests;

public class InMemoryActivityIndexTests
{
    [Fact]
    public void AddEvent_WithProcessKey_CanRetrieveByProcess()
    {
        // Arrange
        var index = new InMemoryActivityIndex();
        var processKey = KeyGenerator.GenerateProcessKey(1, 123, DateTime.UtcNow);
        var activityEvent = new ActivityEvent
        {
            Category = "Process",
            Action = "Start",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref1") },
            SubjectProcess = processKey
        };

        // Act
        index.AddEvent(activityEvent);
        var retrieved = index.GetEventsByProcess(processKey).ToList();

        // Assert (index stores normalized event; compare key properties)
        Assert.Single(retrieved);
        Assert.Equal(activityEvent.Category, retrieved[0].Category);
        Assert.Equal("Start", retrieved[0].Action);
        Assert.Equal(activityEvent.Timestamp, retrieved[0].Timestamp);
        Assert.Equal(processKey, retrieved[0].SubjectProcess);
        Assert.Single(retrieved[0].Evidence);
    }

    [Fact]
    public void AddEvent_WithNetworkFlowKey_CanRetrieveByNetworkFlow()
    {
        // Arrange
        var index = new InMemoryActivityIndex();
        var networkFlowKey = KeyGenerator.GenerateNetworkFlowKey("TCP", "192.168.1.1:8080", "10.0.0.1:443", 123, DateTime.UtcNow);
        var activityEvent = new ActivityEvent
        {
            Category = "Network",
            Action = "Connect",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref1") },
            ObjectNetworkFlow = networkFlowKey
        };

        // Act
        index.AddEvent(activityEvent);
        var retrieved = index.GetEventsByNetworkFlow(networkFlowKey).ToList();

        // Assert (index stores normalized event; compare key properties)
        Assert.Single(retrieved);
        Assert.Equal(activityEvent.Category, retrieved[0].Category);
        Assert.Equal("Connect", retrieved[0].Action);
        Assert.Equal(networkFlowKey, retrieved[0].ObjectNetworkFlow);
        Assert.Single(retrieved[0].Evidence);
    }

    [Fact]
    public void GetEventsByTimeRange_ReturnsEventsInRange()
    {
        // Arrange
        var index = new InMemoryActivityIndex();
        var startTime = DateTime.UtcNow;
        var midTime = startTime.AddMinutes(5);
        var endTime = startTime.AddMinutes(10);
        var outOfRangeTime = startTime.AddMinutes(15);

        var event1 = new ActivityEvent
        {
            Category = "Test",
            Action = "Query",
            Timestamp = startTime,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref1") }
        };

        var event2 = new ActivityEvent
        {
            Category = "Test",
            Action = "Query",
            Timestamp = midTime,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref2") }
        };

        var event3 = new ActivityEvent
        {
            Category = "Test",
            Action = "Query",
            Timestamp = outOfRangeTime,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref3") }
        };

        index.AddEvent(event1);
        index.AddEvent(event2);
        index.AddEvent(event3);

        // Act
        var retrieved = index.GetEventsByTimeRange(startTime, endTime).ToList();

        // Assert (index stores normalized events; match by timestamp and evidence reference)
        Assert.Equal(2, retrieved.Count);
        Assert.Contains(retrieved, e => e.Timestamp == startTime && e.Evidence.Count > 0 && e.Evidence[0].Reference == "ref1");
        Assert.Contains(retrieved, e => e.Timestamp == midTime && e.Evidence.Count > 0 && e.Evidence[0].Reference == "ref2");
        Assert.DoesNotContain(retrieved, e => e.Timestamp == outOfRangeTime);
    }

    [Fact]
    public void GetEventsByCategoryAndAction_ReturnsMatchingEvents()
    {
        // Arrange
        var index = new InMemoryActivityIndex();
        var event1 = new ActivityEvent
        {
            Category = "Process",
            Action = "Start",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref1") }
        };

        var event2 = new ActivityEvent
        {
            Category = "Process",
            Action = "Start",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref2") }
        };

        var event3 = new ActivityEvent
        {
            Category = "Network",
            Action = "Connect",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref3") }
        };

        index.AddEvent(event1);
        index.AddEvent(event2);
        index.AddEvent(event3);

        // Act
        var retrieved = index.GetEventsByCategoryAndAction("Process", "Start").ToList();

        // Assert (index stores normalized events; match by evidence reference)
        Assert.Equal(2, retrieved.Count);
        Assert.Contains(retrieved, e => e.Category == "Process" && e.Action == "Start" && e.Evidence.Count > 0 && e.Evidence[0].Reference == "ref1");
        Assert.Contains(retrieved, e => e.Category == "Process" && e.Action == "Start" && e.Evidence.Count > 0 && e.Evidence[0].Reference == "ref2");
        Assert.DoesNotContain(retrieved, e => e.Evidence.Count > 0 && e.Evidence[0].Reference == "ref3");
    }

    [Fact]
    public void GetEventsByField_ReturnsMatchingEvents()
    {
        // Arrange
        var index = new InMemoryActivityIndex();
        var event1 = new ActivityEvent
        {
            Category = "Test",
            Action = "Query",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref1") },
            Fields = new Dictionary<string, object> { ["Name"] = "Process1" }
        };

        var event2 = new ActivityEvent
        {
            Category = "Test",
            Action = "Query",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref2") },
            Fields = new Dictionary<string, object> { ["Name"] = "Process2" }
        };

        index.AddEvent(event1);
        index.AddEvent(event2);

        // Act
        var retrieved = index.GetEventsByField("Name", "Process1").ToList();

        // Assert (index stores normalized event; compare key fields)
        Assert.Single(retrieved);
        Assert.Equal("Test", retrieved[0].Category);
        Assert.Equal("Query", retrieved[0].Action);
        Assert.True(retrieved[0].Fields.TryGetValue("Name", out var name) && "Process1".Equals(name));
    }

    [Fact]
    public void Clear_RemovesAllEvents()
    {
        // Arrange
        var index = new InMemoryActivityIndex();
        var event1 = new ActivityEvent
        {
            Category = "Test",
            Action = "Query",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref1") }
        };

        index.AddEvent(event1);

        // Act
        index.Clear();

        // Assert
        var retrieved = index.GetEventsByCategory("Test").ToList();
        Assert.Empty(retrieved);
    }

    [Fact]
    public void AddEvent_NormalizesAction_AndStoresOriginalAction()
    {
        // Arrange
        var index = new InMemoryActivityIndex();
        var activityEvent = new ActivityEvent
        {
            Category = "File",
            Action = "Execute",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("Test", "ref1") }
        };

        // Act
        index.AddEvent(activityEvent);
        var retrieved = index.GetEventsByCategory("File").ToList();

        // Assert
        Assert.Single(retrieved);
        Assert.Equal("Open", retrieved[0].Action);
        Assert.Equal("Execute", retrieved[0].Fields["OriginalAction"]);
    }

    [Fact]
    public void Constructor_WithMaxEvents_EnforcesCapacityAndReportsEvicted()
    {
        var index = new InMemoryActivityIndex(5);
        Assert.Equal(5, index.MaxEventCapacity);

        for (var i = 0; i < 10; i++)
        {
            index.AddEvent(new ActivityEvent
            {
                Category = "Test",
                Action = "Evict",
                Timestamp = DateTime.UtcNow.AddSeconds(i),
                Evidence = new List<EvidenceRef> { new EvidenceRef("T", "1") }
            });
        }

        Assert.Equal(5, index.EventCount);
        Assert.Equal(5, index.EvictedEvents);
        var inRange = index.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).ToList();
        Assert.Equal(5, inRange.Count);
    }

    [Fact]
    public void Constructor_WithZero_Unbounded()
    {
        var index = new InMemoryActivityIndex(0);
        Assert.Equal(0, index.MaxEventCapacity);
        for (var i = 0; i < 20; i++)
            index.AddEvent(new ActivityEvent
            {
                Category = "T",
                Action = "A",
                Timestamp = DateTime.UtcNow,
                Evidence = new List<EvidenceRef> { new EvidenceRef("T", "1") }
            });
        Assert.Equal(20, index.EventCount);
        Assert.Equal(0, index.EvictedEvents);
    }
}
