# HostWitness 1.1.0

A live-triage and offline-analysis DFIR tool for Windows. This release roughly doubles the artifact coverage, adds explicit collection-trust signalling, and rebuilds the deep-artifact parsers against real samples with ground-truth validation.

> Distribution is the signed, self-contained single-file `HostWitness.exe` (win-x64, .NET 8 bundled — no runtime install required). The build is signed with a self-signed certificate (`CN=nine-security Inc.`); Windows will show an "unknown publisher" prompt, which is expected.

---

## Highlights

- **Collection Trust dashboard** — a red/amber/green view of how complete and trustworthy an opened snapshot is (capped events, dropped ETW, incomplete artifact copies, limited privilege, non-forensic registry, integrity status). Surfaces caveats that were previously buried in `manifest.json`.
- **Six new attack-chain / persistence artifacts** — scheduled tasks, PowerShell history, BAM/DAM last-execution, startup folders, TaskCache registration times, and WMI subscription persistence.
- **Three deep-artifact parsers** — SRUM, BITS, and WMI persistence — plus a corrected JumpList DestList parser, each validated against real samples.
- **Offline-everywhere** — parse `.evtx`, `SRUDB.dat`, `qmgr.db`, and `OBJECTS.DATA` pulled from a dead box / VSS copy / another tool, not just the live host.
- **"Am I seeing everything?" signalling** — a live status-bar warning when the in-memory index is dropping events, and a cross-volume merged MFT view.

---

## New: collection completeness & trust

- **Collection Trust dashboard (P1)** — shown on snapshot open and via `File ▸ Collection Trust...`. Eleven RAG signals derived from the manifest + integrity check; unknown/older fields are treated as "cannot confirm complete," never as "complete."
- **Live truncation warning (P4)** — a status-bar indicator that turns red when the in-memory index hits its cap and starts permanently dropping the oldest events (which would also be absent from an export), amber when eviction is imminent or only the live render skipped events. Always shown when data is actually being lost.
- **Cross-volume merged MFT view (P4)** — a "Merge all sources" tab that combines all loaded volume/file MFT tabs so you can search and compare across volumes without switching tabs.

## New: persistence & attack-chain artifacts

- **Scheduled tasks** — on-disk Task XML (`%WinDir%\System32\Tasks`): triggers, actions, principal, registration time.
- **TaskCache** — the registry side (`SOFTWARE\…\Schedule\TaskCache`): task path plus creation / last-run / last-success times.
- **PowerShell history** — PSReadLine `ConsoleHost_history.txt`, with conservative suspicious-keyword flagging.
- **BAM/DAM** — per-user last-execution times from the SYSTEM hive.
- **Startup folders** — All-Users and per-user Startup items, with `.lnk` target resolution.
- **WMI persistence** — event-subscription filters, consumers, and bindings (with WQL queries) from the CIM repository `OBJECTS.DATA`.

## New: deep-artifact parsers (validated against real samples)

- **SRUM (`SRUDB.dat`)** — per-app/per-user network data usage, connectivity, and application resource/energy usage. Read via the OS ESE engine (ManagedEsent); validated row-by-row against SrumECmd output.
- **BITS (`qmgr.db`)** — recovers download job URLs and local destination paths from the transfer queue.
- **JumpList DestList — fixed** — the DestList parser is rewritten against the real on-disk layout (verified on a version-6 sample with JLECmd as ground truth), so MRU/last-access timing and target paths now resolve instead of silently falling back.
- **Offline `.evtx` loading** — parse exported event logs; the channel is resolved per record (a `Security.evtx` still maps 4688 → process creation).

## Loading offline evidence

UI menu (`File ▸ …`) and agent flags:

| Artifact | UI | Agent |
|---|---|---|
| Event logs | `Load Event Log (.evtx)...` | `--evtx=<f1,f2,…>` |
| SRUM | `Load SRUM (SRUDB.dat)...` | `--srum=<file>` |
| BITS | `Load BITS (qmgr.db)...` | `--bits=<file>` |
| WMI | `Load WMI (OBJECTS.DATA)...` | `--wmi=<file>` |

Loaded artifacts are tagged `Mode=Offline`, and the source file is bundled into the snapshot's `raw/` folder on export, so the evidence travels with the bundle.

---

## Notes & known limitations

- **ESE databases are read read-only with recovery off** — a dirty-shutdown forensic copy can be parsed without its transaction logs and the evidence file is never modified. The ESE page size is a per-process global, so a single session cannot open two databases with different page sizes (e.g. `SRUDB.dat` and `qmgr.db`); this is surfaced as a clear message, not wrong data. Restart between them if needed.
- **BITS and WMI are conservative string-level triage.** Their on-disk formats are undocumented/version-sensitive binary, so rather than guess struct offsets (which would risk fabricating fields) these extract the high-value readable strings — BITS download URLs/paths, WMI subscription filters/consumers/bindings. WMI does not decode a consumer's command-line/script payload; a recovered name/binding is a pivot, verify the full action with a dedicated CIM tool.
- **SRUM/BITS/WMI are opt-in** (not part of default live collection) because they are high-volume or evidence-specific.
- See `docs/LIMITATIONS.md` for the full per-artifact bounds, and `docs/ASSESSMENT.md` for an objective practical assessment.

## Verifying the download

See [`VERIFY_AND_SMARTSCREEN.md`](VERIFY_AND_SMARTSCREEN.md) for the full SmartScreen and integrity-verification guide.

```
Get-FileHash .\HostWitness.exe -Algorithm SHA256
```

(Compare against the hash published with the release asset.)
