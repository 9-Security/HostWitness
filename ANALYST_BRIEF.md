# HostWitness — Analyst Brief & Positioning

This document summarizes how HostWitness fits into Windows incident response workflows, its practical strengths and limits, and **enhancement goals** targeted for upcoming releases. It is based on the project’s published documentation and source code (source is not distributed in this public repository).

---

## Positioning

HostWitness is best placed on the **front line of Windows incident response**—between **EDR-style live observation** and **full disk or memory forensics**. It is not intended to deliver a final, standalone legal conclusion by itself; it is meant to **quickly answer the questions that matter most on-scene**: what is this host running right now, what happened recently, what persistence is present, which evidence should be preserved immediately, and whether the situation should be escalated to full forensic acquisition.

---

## Practical value

- **Initial intrusion assessment and scoping.** Live Process, Live TCP, ETW, and Event Log help surface active processes, connections, and traces of PowerShell, WMI, Task Scheduler, Sysmon, and related activity.
- **Reconstructing execution chains.** Browser History, Recent LNK, Jump List, Prefetch, Amcache, and Timeline together can link download, open, execution, and user interaction.
- **Persistence hunting.** Offline Hive coverage includes Run/RunOnce, Services, Winlogon, IFEO, StartupApproved, UserAssist, ShimCache, and similar keys—useful for common malware and LOLBins.
- **Dropped files and anti-forensics.** MFT views support in-use vs deleted paths, full paths, timestamp deltas, and time-stomp suspects.
- **Handoff and downstream analysis.** Snapshot / SQLite export is not a raw file dump alone: it carries `modeProfile`, preflight output, `collectionSummary`, drop counts, and known limitations—supporting **defensibility** in reporting.

---

## Strengths

- **Single-pane Windows DFIR workstation:** live triage, offline hive, MFT, snapshot packaging, and portable SQLite fit in one workflow.
- **Clear separation of Forensic Strict vs Triage Fast**, with Live Registry and Create Dump called out as non-forensic / high-risk—useful for knowing what can serve as an evidence baseline vs triage-only aids.
- **Snapshot integrity:** hashes are verified before opening a snapshot; raw / evidence references are constrained to the bundle, reducing contamination during offline review.
- **Sensible MFT acquisition order:** raw volume → Backup privilege → VSS fallback aligns with common DFIR field practice.
- **Analyst-friendly drill-down:** pivoting from a process into the Timeline matches how responders often work—centering on a PID or process chain for correlation.

---

## Limitations

- **Still fundamentally live response.** Documentation states there is no point-in-time consistency; **kernel rootkits, firmware persistence, hypervisor-level attacks, and fully hidden processes** are not reliably detected. Against advanced adversaries it must not be treated as the only evidence source.
- **ETW throttling, bounded ingest queues, UI backpressure, and snapshot/export caps (e.g. ~500k events)** mean that on very noisy hosts **“not seen” does not mean “not present.”**
- **Live Registry and Create Dump** add value but increase **disturbance and dispute risk**; without strict SOP, triage features can be misused as if they were a forensic baseline.
- **MFT:** single-shot size limits (~100 MB), no cross-volume merged view, and unusual record layouts may still need external tools—think **rapid verifier**, not a full NTFS deep dive.
- **Remote Agent** exists but is **outside the current stable scope** in project docs; standardized remote collection at scale is not yet a finished product story.
- **Some high-value artifacts are only partially covered** (e.g. BITS, WMI, SRUM may appear more as registry indicators than full **BITS database**, **WMI repository**, or **SRUDB.dat** parsing).

---

## Enhancement goals for upcoming releases

The items below are **planned strengthening targets** for future versions—not a commitment order or fixed roadmap.

### P1 — High priority

- **Collection trust dashboard:** surface `wasEventCountCapped`, `etwDroppedEventTotal`, `uiBackpressureDroppedTotal`, VSS fallback, and incomplete artifact copies as a **red/amber/green** risk view so analysts do not miss manifest-level caveats.
- **Close common attack-chain gaps:** deepen support for **WMI subscription repository**, **BITS jobs database**, **SRUDB.dat**, **Scheduled Tasks XML**, **Startup folder**, **PowerShell ConsoleHost history**, **BAM/DAM**, and similar artifacts—these often beat adding yet another generic parser for field value.

### P2 — Medium priority

- **Remote collection productization:** stabilize Agent behavior, return validation, centralized intake, retry/resume, signing, and version control to support real multi-host incidents.
- **Analyst-first pivots / detection:** built-in workflows such as **suspicious PowerShell**, **new dropped executables**, **first execution after persistence**, **download-then-execute**, with optional mapping to **MITRE ATT&CK**.

### P3 — Longer term

- **Lower-layer anti-evasion:** direct EVTX / VSS copy paths, more raw-artifact parsers, and richer process/module/thread/handle views—reducing over-reliance on live APIs against userland hooking or high-evasion samples.

---

## One-line summary

HostWitness is a strong **first stop** and **evidence handoff point** for Windows host IR—excelling at **integrated, defensible snapshot workflows**; it inherits **live-tool blind spots** and **uneven depth on some artifacts**, so **high-adversary or large-scale** incidents still need **lower-level acquisition** and **mature remote collection**.

---

## References

Paths refer to the **full project tree** (documentation and source). They are **not** part of this public, documentation-only repository.

| Area | Reference |
|------|-----------|
| Scope / agent | `README.md` (stable scope note) |
| Limits | `docs/LIMITATIONS.md` |
| Operator profile | `docs/StableOperatorProfileAndSOP.md` |
| UI / modes | `WinDFIR.UI/MainWindow.xaml.cs` |
| Event log | `WinDFIR.Providers/EventLogProvider.cs` |
| Offline hive | `WinDFIR.Providers/OfflineHiveRegistryProvider.cs` |
| Snapshot export | `WinDFIR.Core/Snapshot/SnapshotExporter.cs` |
| Collection metadata | `WinDFIR.Core/Snapshot/CollectionMetadataBuilder.cs` |
| Raw disk / MFT path | `WinDFIR.Core/IO/RawDiskReader.cs` |
| Remote Agent | `docs/遠端採集Agent說明.md` |