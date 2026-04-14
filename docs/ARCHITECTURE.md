1. Purpose

This document defines the technical architecture of the Windows Live Response DFIR tool. The tool is designed for portable, single-execution live response evidence collection, prioritizing forensic soundness, minimal system impact, and extensibility.

The tool is intended to be executed directly on a running Windows endpoint without installation or persistence.

2. High-Level Architecture

The tool follows a strictly separated, pipeline-based architecture:

[Collector] → [Parser] → [Analyzer] → [Output]

Each stage has a non-overlapping responsibility boundary.

3. Module Responsibilities
3.1 Collector Layer

Responsibility:

Acquire raw data from the live system

Perform read-only operations only

Do not interpret or correlate data

Typical data sources:

Process, thread, and module information

Network connections and listening sockets

Logged-in users and active sessions

Event log exports (EVTX) and Event Log view events

Registry hives (read-only; live via RegistrySearchProvider, offline via OfflineHiveRegistryProvider with VSS snapshot support)

File system metadata (MFT / USN, where feasible)

VSS (Volume Shadow Copy) is used where available for locked hives/artifacts; fallback to live paths on failure. See VssSnapshotService in Core/Snapshot.

Collectors must:

Detect current privilege level

Gracefully degrade if permissions are insufficient

Log all actions with timestamps

3.2 Parser Layer

Responsibility:

Transform raw artifacts into structured, normalized formats

Preserve original data fidelity

Avoid analytical judgments

Examples:

EVTX → structured JSON

Registry hive → key/value records

MFT records → parsed metadata entries

Parsers must not:

Filter data based on suspicion

Discard records unless explicitly malformed

3.3 Analyzer Layer

Responsibility:

Perform correlation and interpretation

Generate timelines and behavioral indicators

Highlight anomalies with explicit uncertainty

Analyzer outputs may include:

Timeline reconstruction

Persistence mechanism indicators

Lateral movement hints

ATT&CK technique mapping (non-assertive)

Analyzer logic must explicitly distinguish:

Observed facts

Technical inference

Unknown or missing data

4. Execution Model

Portable executable (single file)

No installation or persistence

Single-run execution

Explicit output directory specified at launch

No background services or scheduled tasks

5. Key Components

- **Index**: InMemoryActivityIndex (configurable max events, batch eviction, TrimAllQueues). IActivityIndex for correlation.
- **Snapshot**: VssSnapshotService (pre-check admin/VSS service, WMI error mapping, partial success, HasSnapshotForPath); SnapshotExporter for export.
- **UI state**: MainViewModel holds tab selection (SelectedDynamicTabIndex, SelectedStaticTabIndex) bound to TabControls; exposes CurrentContentKey (derived from indices) so MainWindow drives SharedContentArea and toolbar from a single key; reduces coupling.
- **ViewRegistryService**: View instances and detached-window state are held in WinDFIR.UI.Services.ViewRegistryService; MainWindow registers views and uses the service for Get*View / Detach/Restore, allowing a future Docking implementation to replace the service. See TECH_DEBT §2, §4.
- **Session persistence**: SessionPersistence (Core/Snapshot) saves/restores activity index to %AppData%\HostWitness\last_session on exit/start and via File → Save session now. See TECH_DEBT §3.
- **UI startup**: MainWindow_Loaded forces StaticTabControl.SelectedIndex=0 (System Info), DynamicTabControl.SelectedIndex=-1; default content is System Info.
- **Detached toolbar**: CreateDetachedToolBar builds icon-only buttons (Play/Clear/Refresh/Restore/Resolve/States/TCP·UDP) from DynamicToolBar resources; aligned with main window toolbar.

6. Extensibility

The architecture supports future extension via:

Collector-level modularization

Configuration-driven enable/disable of collectors

Clear interface contracts between stages

7. Technical Debt (Documented)

**Registry provider**: Hybrid. Live API + P/Invoke (RegQueryInfoKey, SafeHandle) in RegistrySearchProvider; marked as transitional. **Current product decision**: retain Live Registry only as an **explicit opt-in non-forensic inspector**, not part of the default stable forensic workflow. **Live** is allowed only when `RegistryLivePolicy.IsLiveRegistryEnabled` is true (dual opt-in in `UiSettings`); UI and Agent use this gate; manifest uses `CollectionMetadataBuilder.IsLiveRegistryEnabled` (forwards to the same policy). Forensics should prefer **Offline Hive** (OfflineHiveRegistryProvider with VSS snapshot). Long term, reevaluate whether to remove Live Registry in a major version. See `docs/TECH_DEBT.md` §1; **product decision priority** (retain vs remove Live path) in `docs/TECH_DEBT.md` **中長期排程** item 1.

**UI coupling**: MainViewModel holds tab selection and exposes **CurrentContentKey**; view instances and Detach state are in **ViewRegistryService** (MainWindow uses the service for resolution). See `docs/TECH_DEBT.md` §2, §4.

**Index persistence**: Session save/restore (last_session) implemented; full persistence (e.g. SQLite) optional and **must follow the pre-implementation decision framework** in `docs/TECH_DEBT.md` §3 (product role, canonical store, schema version, migration, golden samples). **Docking**: ViewRegistryService allows future replacement; full Docking UI optional. See `docs/TECH_DEBT.md` §3–4.

---

*Document last updated: 2026-03-20 (Registry Live formal product decision cross-link); 2026-03-20 (TECH_DEBT §3 Index persistence decision framework cross-link); 2026-03-20 (RegistryLivePolicy gate); 2026-03-05 (ViewRegistryService, SessionPersistence, TECH_DEBT §2–4 update); 2026-02-02 (UI startup, detached toolbar, LIMITATIONS §14).*
