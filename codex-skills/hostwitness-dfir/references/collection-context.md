# HostWitness Collection Context

Read this file before analyzing a HostWitness snapshot, SQLite export, or Agent return.

## Goal

Establish how the data was collected before interpreting what it means. HostWitness output is only as defensible as its collection context.

## First-pass checklist

Inspect these artifacts in order when available:

1. `manifest.json`
2. `hashes.txt`
3. `collect-status-latest.json`
4. `pipeline-verify-report.json`
5. diagnostic export text

## Manifest fields that matter most

### Collection mode and execution context

- `modeProfile`: `forensic_strict`, `triage_fast`, or `custom`
- `registryMode`: expect `offline_only` or `live_non_forensic`
- `registryLiveEnabled`: if true, note that registry findings may come from a non-forensic path
- `preflight.executionContext`: distinguish local UI use from `agent_headless`
- `preflight.enabledProviders`: identify what was actually collected
- `preflight.collectSeconds`: useful for Agent interpretation

### Privilege and VSS context

- `preflight.isAdministrator`
- `preflight.vssServiceRunning`
- `preflight.useVssSnapshots`
- `preflight.outputDirectoryWritable`
- `preflight.availableFreeSpaceBytes`
- `preflight.warnings`
- `preflight.errors`

Lower confidence when the run lacked admin rights, VSS was unavailable, or warnings/errors indicate fallback to live paths.

### Completeness and truncation

- `collectionSummary.sourceEventCount`
- `collectionSummary.exportedEventCount`
- `collectionSummary.eventCap`
- `collectionSummary.wasEventCountCapped`
- `collectionSummary.evidenceReferenceCount`
- `collectionSummary.rewrittenEvidenceReferenceCount`
- `collectionSummary.copiedArtifactFileCount`
- `collectionSummary.skippedEvidenceReferenceCount`
- `collectionSummary.failedEvidenceReferenceCount`
- `collectionSummary.wasArtifactCopyIncomplete`
- `collectionSummary.artifactCopyWarningCount`
- `collectionSummary.preflightWarningCount`
- `collectionSummary.preflightErrorCount`
- `collectionSummary.etwDroppedEventTotal`
- `collectionSummary.uiBackpressureDroppedTotal`
- `etwTotalDrops`
- `knownLimitations`

If events were capped, artifacts failed to copy, or drops occurred, state that clearly before making timeline claims.

## Integrity workflow

### Snapshot only

Use when you have a `snapshot_*` folder with `hashes.txt`:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\VerifySnapshotIntegrity.ps1 -SnapshotPath <snapshot_*>
```

### Agent return or scripted collection

Use when you also have `collect-status-latest.json` and optionally an HMAC key:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\VerifyCollectionPipeline.ps1 -StatusPath <collect-status-latest.json> -SnapshotPath <snapshot_*> [-HmacKey <shared-secret>]
```

If integrity is unverified, failed, or skipped, say so explicitly. Continue analysis only with caveated confidence.

## Time handling

- Check whether the UI or export was using Local or UTC presentation.
- Keep one time basis inside the write-up; if sources mix Local and UTC, normalize before correlating.
- Preserve original timestamps when they are part of the evidence discussion.

## Confidence ladder

Highest confidence:

- Verified snapshot bundle
- Offline or VSS-backed artifacts
- Admin collection with low warning count
- Multiple independent sources telling the same story

Lower confidence:

- Live-only collection
- `triage_fast` or `custom` with aggressive throughput settings
- Live Registry enabled
- Event-cap truncation
- ETW drop counters or UI backpressure drops
- Raw-read truncation warnings
- Artifact copy incomplete or failed references

## Reporting sentence patterns

Use wording like:

- `This snapshot was collected in forensic_strict mode with offline-only registry and verified bundle hashes.`
- `The collection ran without Administrator privileges, so protected artifacts and VSS-backed paths may be incomplete.`
- `The timeline was capped during export, so absence of later events is not conclusive.`
- `Registry findings were collected via a live non-forensic path and should be treated as supportive rather than definitive.`
