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

**Collection trust dashboard (read side):** When opening a snapshot, the UI now parses `manifest.json` and shows a red/amber/green **Collection Trust** window summarizing these caveats so they are not missed: integrity status, bundle completeness, event-cap truncation (`wasEventCountCapped`), ETW drops (`etwDroppedEventTotal`), UI backpressure (`uiBackpressureDroppedTotal`, flagged as live-view-only — it does not affect the persisted timeline per Â§10a), raw artifact copy completeness (`failed`/`skipped`), VSS artifact source, preflight warnings/errors, privilege, and registry mode. Missing fields are shown as `UNKNOWN` (treated as caution). It is re-openable via **File â†’ Collection Trust...**. Assessment logic lives in `WinDFIR.Core/Snapshot/CollectionTrustReport.cs` (unit-tested); display only, no data mutation.

**Diagnostic export (Help â†’ Export diagnostic info):** The generated text file includes ETW throttle total drops (if any) and a "Known limitations" section pointing to this document (file locking, rootkit/API hooking, ETW throttling, UI backpressure).

16. Offline registry: BITS, WMI indicators, SRUM (bounded)

Offline hive enumeration may emit structured fields for registry-only slices under `SOFTWARE\Microsoft\Windows\CurrentVersion\BITS` (BITS client area), `SOFTWARE\Microsoft\WBEM\CIMOM` (CIMOM configuration values), `SYSTEM\ControlSet00x\Control\WMI\Security` (per-namespace security storage), and `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SRUM` (registry subtree when present). These are **indicators of configuration or presence from an offline hive**, not proof of transfers, execution, or malicious WMI event subscriptions. **BITS:** there is no decode of the full BITS job database or transfer log from files here—only values visible under the registry path. **WMI persistence:** the tool does **not** enumerate `ROOT\subscription` `__EventFilter` / `__EventConsumer` / `__FilterToConsumerBinding` MOF or repository objects; CIMOM and namespace-security keys are supplementary context only. **SRUM:** `SRUDB.dat` and ESE application/resource tables are **not** parsed; only the SRUM-related registry path is surfaced when the hive contains it. Prefer corroboration with event logs, disk artifacts, and live or exported `SRUDB.dat` analysis in other tools when needed.

17. Scheduled Tasks (on-disk XML)

`ScheduledTaskProvider` parses Task Scheduler **XML definitions** under `%WinDir%\System32\Tasks` (recursively) and emits one `Persistence` / `ScheduledTask` event per task with triggers, actions (Exec command/args, ComHandler ClassId), principal (UserId/RunLevel/LogonType), and registration metadata. Files are read as UTF-16 (the real on-disk encoding). **Bounds:** this parses the **on-disk XML only** — it does **not** decode the registry `TaskCache` store (`SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache`, including the binary `SD`/`DynamicInfo` values) and does not reconcile XML against TaskCache, so a task present only in the registry (or a deleted task's TaskCache remnant) may not appear. Access to `System32\Tasks` may require Administrator; otherwise some tasks are skipped (reported via collection warnings). Timestamps prefer RegistrationInfo `Date`, then the earliest trigger `StartBoundary`, then file last-write time.

18. PowerShell history (PSReadLine)

`PowerShellHistoryProvider` reads each user profile's `AppData\Roaming\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt` and emits one `PowerShell` / `ConsoleHostHistory` event per command line, with the user, command text, and any matched well-known offensive-PowerShell keywords (`SuspiciousKeywords` — a heuristic triage aid, **not** proof). **Bounds:** PSReadLine history has **no per-line timestamps**, so every event from a file shares that file's last-write time. The format does not record success/failure — a line means the command was *accepted into history*, not that it ran successfully (events are emitted at Confidence=Medium). **Multi-line commands** are stored as multiple physical lines with no escaping and cannot be reliably reassembled, so each non-empty physical line is treated as one entry. Only PSReadLine console history is covered — this is not transcript logging, Script Block Logging (Event ID 4104), or Module Logging; corroborate with those when available. Reading other users' profiles may require Administrator.

19. BAM / DAM last-execution

Offline SYSTEM-hive enumeration now decodes Background/Desktop Activity Moderator entries under `...\Services\bam` (and `dam`) `...\UserSettings\<SID>`. Each executable value's first 8 bytes are an 8-byte FILETIME of **last execution**; the decoded event carries `OfflineHiveDecoded=BAM`/`DAM`, `BamExecutablePath`, `BamUserSid`, `BamLastExecutionUtc`, and is timestamped at the execution time. **Bounds:** BAM records only the **most recent** execution per executable per user (not a run count or full history), the path is the raw NT device path (e.g. `\Device\HarddiskVolumeN\...`, not drive-letter-normalized), and BAM presence/behavior varies by Windows build. Corroborate with Prefetch, Amcache, UserAssist, and event logs. Decoding rides the existing recursive `Services` query (no extra hive read).

20. Startup folders

`StartupFolderProvider` enumerates the All-Users (`ProgramData\...\Start Menu\Programs\StartUp`) and per-user Startup folders and emits a `Persistence` / `StartupFolder` event per entry, resolving `.lnk` shortcut targets via the LNK parser. **Bounds:** only the file-system Startup folders are covered (registry Run/RunOnce/Winlogon persistence is handled separately by the offline hive provider); `desktop.ini` is ignored; the entry timestamp is the file's last-write time (when the item was placed), not an execution time. Reading other users' Startup folders may require Administrator.

21. Scheduled Task registration (TaskCache)

Offline SOFTWARE-hive enumeration decodes the Scheduled Task registration cache under `Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{GUID}`. Each per-task key's `Path` value (the task tree path) and the FILETIMEs embedded in its `DynamicInfo` value are surfaced as `OfflineHiveDecoded=TaskCache` events carrying `TaskCache_Guid`, `TaskCache_Path`, `TaskCache_CreatedUtc`, `TaskCache_LastRunUtc`, and (on newer builds) `TaskCache_LastSuccessUtc`; the DynamicInfo event is timestamped at last-run (else creation). **Bounds:** this decodes only the registration metadata, **not** the task's command line — the binary `Actions`/`Triggers` blobs are deliberately left unparsed (the on-disk Task XML provider, §17, already provides the command; a mis-parsed action blob would inject false evidence). `DynamicInfo` byte offsets vary by Windows build, so every FILETIME read is range-checked (year 2000–2100): a wrong offset on an unexpected layout yields a missing field, never a fabricated timestamp. Correlate the GUID with the on-disk Task XML and the `Schedule\TaskCache\Tree` registry path to confirm a task. Decoding rides the existing recursive TaskCache query (no extra hive read).

22. Offline event log (.evtx) loading

The event log provider can parse exported `.evtx` files (from a dead box, VSS copy, or another tool) instead of the live host's channels — `File ▸ Load Event Log (.evtx)...` in the UI, or `--evtx=<file1,file2,...>` for the agent. When any `.evtx` is supplied the provider runs in **offline-only mode** (it does not also read the live host). Each record's channel is resolved from the record itself, so a `Security.evtx` still maps event ID 4688 to Process/Start; events are tagged `Mode=Offline` and `OfflineSource=<path>`, and the source `.evtx` is bundled into the snapshot's `raw/evtx/` on export so the evidence travels with the bundle. **Bounds:** reading uses the Windows Eventing API (`EventLogReader` with `PathType.FilePath`), so it requires the OS to be able to open the file — a corrupt/dirty `.evtx` (not cleanly closed) may need `wevtutil` recovery first, and channels with custom message DLLs render generic text without the provider installed; the existing per-log cap (20,000 records) applies per file; offset/format edge cases are the OS parser's, not ours.

23. SRUM (System Resource Usage Monitor)

The SRUM provider parses `SRUDB.dat` (an ESE database) — `File ▸ Load SRUM (SRUDB.dat)...` in the UI, or `--srum=<file>` for the agent — and emits one event per provider-table row: per-app/per-user **network data usage** (bytes sent/received), network connectivity, and application resource/energy usage, each timestamped. `AppId`/`UserId` integers are resolved through `SruDbIdMapTable` to application strings and user SIDs. Reading uses ManagedEsent (the OS `esent.dll` engine), and the database is copied to a temp file and attached **read-only with recovery off**, so a dirty-shutdown forensic copy can be read without its transaction logs and without ever modifying the evidence. The source `SRUDB.dat` is bundled into `raw/srum/` on export. **Bounds:** SRUM aggregates usage into ~hourly buckets, so timestamps are bucket boundaries, not exact event times; values are cumulative counters maintained by Windows; only the well-known provider tables are decoded (other GUID tables are skipped); each table is capped at 100,000 rows (with a truncation warning) because SRUM is high-volume; reading requires the OS ESE engine to open the page size/format (validated against SrumECmd output on a real database). SRUM is **opt-in** (not part of default live collection).

24. BITS (Background Intelligent Transfer Service)

The BITS provider recovers queued transfer entries from `qmgr.db` (an ESE database) — `File ▸ Load BITS (qmgr.db)...` in the UI, or `--bits=<file>` for the agent — and emits one event per `Files`/`Jobs` record (download events carry the source URL and local destination path; jobs carry the job name and owner SID). The source `qmgr.db` is bundled into `raw/bits/` on export. **Bounds:** in the modern `qmgr.db` format each row stores its data in a single undocumented, version-dependent binary blob, so rather than guess struct offsets (which would risk fabricating fields) the parser performs **conservative UTF-16 string extraction** — it pulls the readable strings (URLs, local/UNC paths, job names, SIDs) out of each blob and classifies them, but does **not** decode byte counts, transfer state, or per-file ordering, and does not recover the per-transfer timestamps (events are anchored at the `qmgr.db` file's last-write time, not the transfer time). The ESE page size is a per-process global, so a session that already opened a different-page-size ESE database (e.g. SRUM's `SRUDB.dat`) cannot also open `qmgr.db` until the app is restarted; this surfaces as a clear warning rather than wrong data. Validated against a real `qmgr.db` (recovers Firefox/Chrome updater download URLs and destinations).

25. WMI persistence (OBJECTS.DATA)

The WMI provider recovers event-subscription persistence from the CIM repository `OBJECTS.DATA` — `File ▸ Load WMI (OBJECTS.DATA)...` in the UI, or `--wmi=<file>` for the agent — and emits one `Persistence` event per `__EventFilter` (with its WQL query), event consumer, and `__FilterToConsumerBinding` (the consumer↔filter tie). The source `OBJECTS.DATA` is bundled into `raw/wmi/` on export. **Bounds:** fully parsing the CIM repository (classes/instances via INDEX.BTR/MAPPING) is large and version-sensitive, so — like PyWMIPersistenceFinder — this is a **triage-level string extraction**: it matches the readable instance patterns (`<Class>EventConsumer.Name="…"`, `__EventFilter` + WQL query, binding references) and deliberately ignores class definitions / provider registrations (which carry property declarations but no instance values), so it reports the subscription picture, not schema noise. It does **not** decode the consumer's command-line/script **payload** (that requires a real CIM parse such as python-cim) — a recovered consumer name/binding is a pivot, not the full action. Validated against a real `OBJECTS.DATA` (recovers the default `SCM Event Log` subscription without emitting the dozen class-definition hits as findings). WMI is **opt-in** (not part of default live collection).

26. Cross-source anomaly detection (live vs offline)

A live tool cannot guarantee it sees an object a kernel/firmware implant hides, but a discrepancy between what a **live API** reports and what the **raw/offline** source shows is a high-value tampering/hiding indicator. `CrossSourceAnomalyDetector` compares the two views and emits `Category="Anomaly"` events. Two applications ship: **services** — the live `LiveServiceProvider` (WMI `Win32_Service`) versus the raw SYSTEM-hive Services; and **scheduled tasks** — the on-disk Task XML (`%WinDir%\System32\Tasks`) versus the registry `TaskCache`. A service/task in the raw source but missing from the other view (`MissingFromLive`) is a possible hiding indicator (e.g. a task whose XML was deleted while TaskCache still runs it); the reverse (`MissingFromOffline`) is a possible memory-only/injected or just-created item. Both run automatically in the agent (after collection, before export) and on demand in the UI (`Advanced ▸ Cross-Source Anomalies...`). **Bounds:** findings are advisory **Amber/Low confidence** ("investigate"), never "confirmed rootkit" — benign causes exist (disabled or recently-removed services, collection timing). The offline set is filtered to user-mode (Win32) services because WMI does not enumerate kernel drivers (comparing them would be coverage noise). It is a no-op unless BOTH sources are present (the offline SYSTEM hive needs Administrator/VSS on a live host). Crucially, **WMI is itself a user-mode API**: a sufficiently privileged ring-0/firmware implant can lie to both WMI and the raw read, so this raises the adversary's cost and catches common hiding, but is a tripwire and corroboration aid — not a guarantee. (Presence is compared; image-path value comparison is deferred pending path normalization.)

---

*Document last updated: 2026-04-14 (§4 snapshot export fails if raw files cannot be hashed for `hashes.txt`); 2026-04-13 (§16 offline BITS/WMI/SRUM registry bounds; sections 9a / 10 / 13: BurstQueue ingest visibility; large export/SQLite stability; MFT partial-source UI clarity); 2026-03-19 (Â§9a / Â§13 note the explicit 100 MB truncation warning and `Load MFT file...` record-size auto-detection); 2026-03-18 (Â§13 MFT now uses per-source tabs, `Load from Volumes...`, and no refresh re-run); 2026-03-17 (Â§13 MFT now uses raw volume â†’ Backup privilege â†’ VSS ordered fallback); 2026-03-16 (Â§9a export/session caps); 2026-03-05 (Â§10a UI queue backpressure, Â§15 export visibility); 2026-02-02 (Â§13 Raw Disk, Â§14 risk mitigations).*


