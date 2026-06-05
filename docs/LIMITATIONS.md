1. Live Response Limitations

Due to the nature of live response:

Collected data cannot represent a perfectly consistent system state

Volatile artifacts may change during acquisition

Some evidence may be overwritten before collection

2. Visibility Limitations

The tool cannot reliably detect:

Kernel-level rootkits

Firmware-level persistence

Hypervisor-based attacks

Fully hidden processes using advanced anti-forensics

3. Privilege-Related Limitations

Without elevated privileges, the tool may be unable to:

Access certain registry hives

Export protected event logs

Enumerate all system handles or drivers

These limitations must be explicitly reported in output.

Note on ETW throttling:

For stability, high-frequency ETW events (File/Registry/Network) may be throttled, which can cause some events to be dropped.

Separately, captures accepted from ETW into an internal bounded ingest queue are drained on normal shutdown; if the queue is saturated under burst load, additional captures are dropped and counted under a **BurstQueue** category. Those drops are included in ETW drop totals and surfaced to operators through the same mechanisms as per-category throttle reporting (including periodic system/query events and last-reported drop stats), not only as silent totals.

4. Integrity Limitations

Execution of the tool inherently alters system memory

Some artifacts may be affected by the act of observation

Perfect forensic isolation is not achievable on live systems

Snapshot open now verifies `hashes.txt` when present and warns before loading unverified or failed bundles. Imported `raw/` evidence references are also contained to the snapshot `raw/` directory and are no longer resolved outside the bundle.

Snapshot export **fails** (rather than emitting an incomplete `hashes.txt`) if any file under `raw/` cannot be read for SHA-256 hashing after copy, so integrity listing stays consistent with bundle contents.

5. Scope Limitations

The tool is not designed to:

Perform remediation or containment

Remove malware

Kill processes or services

Modify system configuration

6. Legal and Operational Limitations

Output must be interpreted by trained personnel

The tool does not replace full disk or memory imaging

Results must be corroborated with additional forensic methods

7. Live TCP View Data Limits

Sent/Recv Packets and Sent/Recv Bytes are populated from ETW Kernel-Network events when TransferSize/Size and Send/Recv opcode are present; otherwise they remain 0. NetworkStatsAggregator aggregates per-connection stats; Live TCP View merges them on refresh. DNS resolve results depend on network availability and system resolver configuration.

8. VSS Snapshot Limits

VSS requires Administrator elevation and the Volume Shadow Copy service (VSS) to be running. The tool performs pre-checks (IsRunningAsAdmin, IsVssServiceRunning) and reports clear warnings. WMI Create return codes (1â€“12) are mapped to readable messages (e.g. Access denied, Volume not found, Maximum shadow copies reached). When snapshot creation fails for a volume, the tool falls back to live paths for that volume; partial success is supported (some volumes from snapshot, others live). Multiple volumes are snapshotted separately (not a single point-in-time set); a consistency warning is shown. Use HasSnapshotForPath to determine whether a path is from VSS or live.

9. InMemory Index Limits

The in-memory index uses a configurable cap (Index.MaxEvents in settings.json and Settings UI; 0 = unbounded). Older events are evicted in batches; TrimAllQueues periodically reclaims memory from secondary indexes. Evicted-event counts are reported in the UI and in system events.

9a. Export and Session Save Caps

Snapshot export and session save load events with an upper bound (500,000 events per run) to avoid excessive memory use. Very large timelines may be truncated in a single export/save. Session restore back into the live in-memory index is still bounded by `Index.MaxEvents`; if that cap is lower than the saved session, the newest events are retained and the UI warns before restoring. Raw disk read (RawDiskReader.ReadSectors / ReadBytes) and PhysicalDrive-based MFT load by drive letter are capped at 100 MB per call to prevent OOM; when an MFT source hits that cap, the MFT tab status now explicitly warns that only the first segment was loaded and results may be incomplete. Snapshot export writes `timeline.json` incrementally (same JSON shape) to lower peak memory during large exports; SQLite index export batches row inserts within a single transaction to improve stability on large event sets without changing schema or file semantics.

10. ETW Throttling Visibility

ETW drop counts are reported in the status bar and via system events; high-frequency events can still be lost when throttled. Ingest-queue saturation (**BurstQueue**) uses the same class of visibility (totals, last-reported drops, and system events) as throttle-path drops.

10a. UI Queue Backpressure

When event producer throughput exceeds UI render throughput, the UI queue uses backpressure and may drop excess queued render events to keep the interface responsive. This does not remove events already persisted in the activity index, but it can reduce live visual completeness during bursts.

11. Process Tree Completeness

Process tree rendering depends on captured process start events. If capture starts late, parent/ancestor processes may be missing.

12. Process Memory Dump (Create Dump)

Live Process right-click **Create Dump > Create minidump / Create full dump** uses MiniDumpWriteDump (DbgHelp). Dumping other processes requires Administrator; same-user processes may work without elevation. Minidump = stack/modules (smaller file); full dump = full process virtual memory (large file). Output is a single process dump, not host physical memory.

13. Raw Disk / Offline Consistency

Raw disk read is available via RawDiskReader (ReadSectors/ReadBytes from \\.\PhysicalDriveN; requires Administrator; single read capped at 100 MB) and OfflineHiveRegistryProvider.AddRawHive(driveNumber, offsetBytes, sizeBytes, hiveName) when offset/size are known. MFT can be loaded by selecting one or more drive letters in Load from Volumes...; each selected volume opens in its own tab and uses raw volume first, then Backup privilege direct `$MFT`, and finally VSS snapshot, while keeping the per-read cap at 100 MB. `Load MFT file...` now auto-detects common exported MFT record sizes (1024 / 4096 bytes), but very unusual record layouts may still need external validation. There is intentionally no merged All view; cross-volume comparison is manual by tab, and CSV/JSON export applies only to the selected tab. If the source exceeds the 100 MB read cap, parsing still proceeds on the partial bytes and the UI surfaces an explicit **PARTIAL / CAPPED MFT SOURCE** status prefix plus a note with loaded bytes, logical `$MFT` size when known (and approximate percent), or that the total size could not be determined; this is separate from the MFT tab display entry cap (large parses may still be list-truncated for responsiveness). Offline hive analysis otherwise uses VSS snapshot or live paths. Usage details: `docs\RawDiskä½¿ç”¨èªªæ˜Ž.md`.

14. Risk Mitigations (Summary)

| Risk | Mitigation |
|------|------------|
| File locking | VSS snapshot for locked hives; fallback to live path; Admin + VSS service required. |
| Rootkit / API hooking | Documented limitation; raw registry/MFT parsing is the only reliable mitigation (future). |
| Memory (OOM) | Configurable Index.MaxEvents; eviction; Evicted count in UI. |
| ETW high-frequency loss | Throttling and bounded ingest queue with visible drop counts (including BurstQueue); configurable throttle in settings. |

15. Export and Diagnostic Visibility of Risks

**Snapshot export (manifest.json):** When exporting from the UI or Agent, the manifest includes collection metadata such as `modeProfile`, `preflight` (`executionContext`, `isAdministrator`, `vssServiceRunning`, `useVssSnapshots`, `timeZoneDisplay`, `enabledProviders`, `outputDirectory`, `outputDirectoryWritable`, `availableFreeSpaceBytes`, `minimumRecommendedFreeSpaceBytes`, `warnings`, `errors`, and `collectSeconds` when available), `collectionSummary` (`sourceEventCount`, `exportedEventCount`, `eventCap`, `wasEventCountCapped`, `evidenceReferenceCount`, `rewrittenEvidenceReferenceCount`, `copiedArtifactFileCount`, `skippedEvidenceReferenceCount`, `failedEvidenceReferenceCount`, `wasArtifactCopyIncomplete`, `usedVssSnapshotForArtifactCopy`, `artifactCopyWarningCount`, `preflightWarningCount`, `preflightErrorCount`, `etwDroppedEventTotal`, `uiBackpressureDroppedTotal`), `registryMode` / `registryLiveEnabled`, and `knownLimitations` (short text for file locking/VSS, rootkit/API hooking, ETW throttling, UI backpressure). UI exports now run a preflight check for output-directory writability, free space, and VSS/admin state before export begins. This allows consumers of the snapshot to see collection-time risks, pre-export operational warnings, event-cap truncation state, artifact-copy completeness, and the execution context used to produce the bundle.

**Diagnostic export (Help â†’ Export diagnostic info):** The generated text file includes ETW throttle total drops (if any) and a "Known limitations" section pointing to this document (file locking, rootkit/API hooking, ETW throttling, UI backpressure).

16. Offline registry: BITS, WMI indicators, SRUM (bounded)

Offline hive enumeration may emit structured fields for registry-only slices under `SOFTWARE\Microsoft\Windows\CurrentVersion\BITS` (BITS client area), `SOFTWARE\Microsoft\WBEM\CIMOM` (CIMOM configuration values), `SYSTEM\ControlSet00x\Control\WMI\Security` (per-namespace security storage), and `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SRUM` (registry subtree when present). These are **indicators of configuration or presence from an offline hive**, not proof of transfers, execution, or malicious WMI event subscriptions. **BITS:** there is no decode of the full BITS job database or transfer log from files here—only values visible under the registry path. **WMI persistence:** the tool does **not** enumerate `ROOT\subscription` `__EventFilter` / `__EventConsumer` / `__FilterToConsumerBinding` MOF or repository objects; CIMOM and namespace-security keys are supplementary context only. **SRUM:** `SRUDB.dat` and ESE application/resource tables are **not** parsed; only the SRUM-related registry path is surfaced when the hive contains it. Prefer corroboration with event logs, disk artifacts, and live or exported `SRUDB.dat` analysis in other tools when needed.

---

*Document last updated: 2026-04-14 (§4 snapshot export fails if raw files cannot be hashed for `hashes.txt`); 2026-04-13 (§16 offline BITS/WMI/SRUM registry bounds; sections 9a / 10 / 13: BurstQueue ingest visibility; large export/SQLite stability; MFT partial-source UI clarity); 2026-03-19 (Â§9a / Â§13 note the explicit 100 MB truncation warning and `Load MFT file...` record-size auto-detection); 2026-03-18 (Â§13 MFT now uses per-source tabs, `Load from Volumes...`, and no refresh re-run); 2026-03-17 (Â§13 MFT now uses raw volume â†’ Backup privilege â†’ VSS ordered fallback); 2026-03-16 (Â§9a export/session caps); 2026-03-05 (Â§10a UI queue backpressure, Â§15 export visibility); 2026-02-02 (Â§13 Raw Disk, Â§14 risk mitigations).*


