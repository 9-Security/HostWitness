using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

public class CollectionTrustAssessorTests
{
    private static CollectionTrustSignal Signal(CollectionTrustReport report, string name) =>
        Assert.Single(report.Signals, s => s.Name == name);

    [Fact]
    public void CleanForensicManifest_IsGreen()
    {
        const string manifest = """
        {
          "complete": true,
          "toolVersion": "1.2.0",
          "collectionTime": "2026-06-06T00:00:00Z",
          "registryMode": "offline_only",
          "registryLiveEnabled": false,
          "modeProfile": "forensic_strict",
          "host": { "hostname": "WS01" },
          "collectionSummary": {
            "sourceEventCount": 1000,
            "exportedEventCount": 1000,
            "eventCap": 500000,
            "wasEventCountCapped": false,
            "evidenceReferenceCount": 5,
            "copiedArtifactFileCount": 5,
            "skippedEvidenceReferenceCount": 0,
            "failedEvidenceReferenceCount": 0,
            "wasArtifactCopyIncomplete": false,
            "usedVssSnapshotForArtifactCopy": true,
            "etwDroppedEventTotal": 0,
            "uiBackpressureDroppedTotal": 0
          },
          "preflight": { "isAdministrator": true, "warnings": [], "errors": [] }
        }
        """;

        var report = CollectionTrustAssessor.AssessManifestJson(manifest, SnapshotIntegrityStatus.Verified);

        Assert.True(report.ManifestPresent);
        Assert.Equal(CollectionTrustLevel.Green, report.OverallLevel);
        Assert.Equal("WS01", report.Hostname);
        Assert.Equal(1000, report.ExportedEventCount);
        Assert.All(report.Signals, s => Assert.Equal(CollectionTrustLevel.Green, s.Level));
    }

    [Fact]
    public void CappedEvents_AreRed_WithLostCount()
    {
        const string manifest = """
        {
          "complete": true,
          "collectionSummary": {
            "sourceEventCount": 600000,
            "exportedEventCount": 500000,
            "eventCap": 500000,
            "wasEventCountCapped": true
          }
        }
        """;

        var report = CollectionTrustAssessor.AssessManifestJson(manifest);

        var sig = Signal(report, "Event truncation");
        Assert.Equal(CollectionTrustLevel.Red, sig.Level);
        Assert.Contains("100,000", sig.Detail);
        Assert.Equal(CollectionTrustLevel.Red, report.OverallLevel);
    }

    [Fact]
    public void FailedArtifactCopy_IsRed_SkippedIsAmber()
    {
        const string failed = """
        { "complete": true, "collectionSummary": { "failedEvidenceReferenceCount": 3, "skippedEvidenceReferenceCount": 0 } }
        """;
        var failedReport = CollectionTrustAssessor.AssessManifestJson(failed);
        Assert.Equal(CollectionTrustLevel.Red, Signal(failedReport, "Raw artifact copy").Level);

        const string skipped = """
        { "complete": true, "collectionSummary": { "failedEvidenceReferenceCount": 0, "skippedEvidenceReferenceCount": 2, "wasArtifactCopyIncomplete": true } }
        """;
        var skippedReport = CollectionTrustAssessor.AssessManifestJson(skipped);
        Assert.Equal(CollectionTrustLevel.Amber, Signal(skippedReport, "Raw artifact copy").Level);
    }

    [Fact]
    public void EtwDrops_AreAmber_ButUiBackpressureNotedAsNonDataLoss()
    {
        const string manifest = """
        {
          "complete": true,
          "collectionSummary": { "etwDroppedEventTotal": 1500, "uiBackpressureDroppedTotal": 200 }
        }
        """;

        var report = CollectionTrustAssessor.AssessManifestJson(manifest);

        var etw = Signal(report, "ETW completeness");
        Assert.Equal(CollectionTrustLevel.Amber, etw.Level);
        Assert.Contains("1,500", etw.Detail);

        var ui = Signal(report, "UI render backpressure");
        Assert.Equal(CollectionTrustLevel.Amber, ui.Level);
        Assert.Contains("unaffected", ui.Detail);
    }

    [Fact]
    public void NotComplete_IsRed()
    {
        var report = CollectionTrustAssessor.AssessManifestJson("""{ "complete": false }""");
        Assert.Equal(CollectionTrustLevel.Red, Signal(report, "Bundle completeness").Level);
        Assert.Equal(CollectionTrustLevel.Red, report.OverallLevel);
    }

    [Fact]
    public void NonAdminAndLiveRegistry_AreAmber()
    {
        const string manifest = """
        {
          "complete": true,
          "registryMode": "live_non_forensic",
          "preflight": { "isAdministrator": false, "warnings": [], "errors": [] }
        }
        """;

        var report = CollectionTrustAssessor.AssessManifestJson(manifest);
        Assert.Equal(CollectionTrustLevel.Amber, Signal(report, "Privilege").Level);
        Assert.Equal(CollectionTrustLevel.Amber, Signal(report, "Registry mode").Level);
        Assert.Equal(CollectionTrustLevel.Amber, report.OverallLevel);
    }

    [Fact]
    public void PreflightErrors_AreRed()
    {
        const string manifest = """
        { "complete": true, "preflight": { "isAdministrator": true, "errors": ["Output directory preflight failed: access denied"], "warnings": [] } }
        """;
        var report = CollectionTrustAssessor.AssessManifestJson(manifest);
        Assert.Equal(CollectionTrustLevel.Red, Signal(report, "Preflight").Level);
    }

    [Fact]
    public void MissingFields_AreUnknownAndTreatedAsAmber()
    {
        // Minimal manifest: most signals cannot be determined -> Unknown -> Amber aggregation.
        var report = CollectionTrustAssessor.AssessManifestJson("""{ }""");

        Assert.True(report.ManifestPresent);
        var etw = Signal(report, "ETW completeness");
        Assert.True(etw.IsUnknown);
        Assert.Equal(CollectionTrustLevel.Amber, etw.Level);
        Assert.Equal(CollectionTrustLevel.Amber, report.OverallLevel);
    }

    [Fact]
    public void InvalidJson_ReportsMissingManifestRed()
    {
        var report = CollectionTrustAssessor.AssessManifestJson("not json {");
        Assert.False(report.ManifestPresent);
        Assert.Equal(CollectionTrustLevel.Red, report.OverallLevel);
    }

    [Fact]
    public void IntegrityFailure_DrivesOverallRed_EvenWithCleanSummary()
    {
        const string manifest = """{ "complete": true, "preflight": { "isAdministrator": true } }""";
        var report = CollectionTrustAssessor.AssessManifestJson(manifest, SnapshotIntegrityStatus.Failed);
        Assert.Equal(CollectionTrustLevel.Red, Signal(report, "Integrity").Level);
        Assert.Equal(CollectionTrustLevel.Red, report.OverallLevel);
    }

    [Fact]
    public void KnownLimitations_AreReadFromObjectOrArray()
    {
        const string asObject = """
        { "complete": true, "knownLimitations": { "etw": "throttling may drop events", "rootkit": "not detected" } }
        """;
        var objReport = CollectionTrustAssessor.AssessManifestJson(asObject);
        Assert.Equal(2, objReport.KnownLimitations.Count);

        const string asArray = """
        { "complete": true, "knownLimitations": ["a", "b", "c"] }
        """;
        var arrReport = CollectionTrustAssessor.AssessManifestJson(asArray);
        Assert.Equal(3, arrReport.KnownLimitations.Count);
    }

    [Fact]
    public void AssessFolder_MissingManifest_IsRed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_TrustNoManifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var report = CollectionTrustAssessor.AssessFolder(tempDir);
            Assert.False(report.ManifestPresent);
            Assert.Equal(CollectionTrustLevel.Red, report.OverallLevel);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AssessFolder_ReadsManifestFromSnapshotSubdirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_TrustFolder_" + Guid.NewGuid().ToString("N"));
        var snapshotDir = Path.Combine(tempDir, "snapshot_20260606_000000");
        Directory.CreateDirectory(snapshotDir);
        try
        {
            File.WriteAllText(Path.Combine(snapshotDir, "manifest.json"),
                """{ "complete": true, "collectionSummary": { "wasEventCountCapped": false }, "preflight": { "isAdministrator": true } }""");

            var report = CollectionTrustAssessor.AssessFolder(tempDir);
            Assert.True(report.ManifestPresent);
            Assert.Equal(snapshotDir, report.SnapshotDirectory);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
