# Stable Operator Profile and SOP

This document provides operator-facing guidance for the HostWitness UI/Core local stable workflow: when to use each profile, when to allow high-risk actions, and how the UI classifies actions. It supports Phase 3 (operational consistency) of the stable roadmap.

**Related docs:** [Stable boundary](ç©©å®šç‰ˆé‚Šç•Œå®šç¾©.md), [Roadmap](ç©©å®šç‰ˆè·¯ç·šåœ–.md), [LIMITATIONS.md](LIMITATIONS.md).

---

## 1. Operator profiles

### 1.1 Forensic Strict

- **Purpose:** Formal evidence collection, report-ready output, high defensibility.
- **Settings:** Apply via **Advanced > Settings** and use the **Forensic Strict** profile (or ensure: Registry use offline only; Live Registry disabled; ETW throttle 300/s; Index 200k events; Event Log / ETW TTL and caps per boundary doc).
- **When to use:**
  - When the outcome may be used in formal reports or legal proceedings.
  - When you need to state that registry and artifact paths followed forensic-safe defaults.
  - When you want preflight and collection summary to reflect offline-first, conservative throttling.
- **Stable default:** This is the recommended default for the stable forensic workflow. Do not describe Triage Fast as the forensic default.

### 1.2 Triage Fast

- **Purpose:** Live triage only; wider live visibility and faster iteration. **Not forensic-safe default.**
- **Settings:** Apply via **Advanced > Settings** and use the **Triage Fast** profile (Live Registry experimental enabled, higher ETW throttle, lower index cap, etc.).
- **When to use:**
  - Internal triage, quick assessment, or non-legal contexts where live visibility is prioritized.
  - When you explicitly accept that results are not suitable as sole forensic baseline and that Live Registry is non-forensic.
- **Do not:** Use Triage Fast as the default for stable forensic workflow. Do not describe it as forensic-safe in UI or docs.

### 1.3 Default stable workflow (recommended path)

- **Default path:** Start with **Forensic Strict** (or equivalent settings). Use **Export Snapshot** after collection; rely on **preflight** and **collectionSummary** in `manifest.json` for defensibility.
- **Offline-first:** Prefer Offline Hive (Settings: Registry use offline only). Use **Load from Volumes...** / **Load MFT file...** and VSS where available (Admin + VSS service).
- **High-risk actions:** Do not use **Create Dump** or **Registry Search (Live)** as part of the default stable forensic path. Use them only when explicitly allowed by your SOP and when the risk of altering or observing live state is accepted.

---

## 2. SOP / runbook summary

| Situation | Action |
|-----------|--------|
| Formal evidence / report | Use **Forensic Strict**. Export Snapshot; keep preflight and collectionSummary. Do not enable Live Registry or use Create Dump in the default flow. |
| Quick live triage (internal) | May use **Triage Fast**. Do not treat output as forensic baseline; document that Live Registry was used if applicable. |
| Need offline registry | Use **Offline Hive** (Settings: only offline; add VSS or Raw Hive paths as needed). Prefer VSS for locked hives (Admin + VSS service). |
| Need MFT from disk | Use **MFT** tab: **Load from Volumes...** (raw -> Backup privilege -> VSS) or **Load MFT file...**. Admin recommended; note 100 MB cap and tab warnings. |
| Process memory dump requested | Use **Create Dump** only when authorized. This is **high-risk** (alters/observes process state); not part of default stable forensic workflow. Document in your runbook. |
| Live registry search requested | Use **Advanced > Registry Search...** only when Live Registry is explicitly enabled and non-forensic use is accepted. For forensic use prefer Offline Hive. |

---

## 3. UI action classification

The following table classifies UI actions as **forensic** (forensic-preferred baseline), **non-forensic / high-risk**, or **advanced** (configuration, diagnostics, or power-user). This supports isolating high-risk actions from the default stable workflow.

| UI action | Classification | Notes |
|-----------|----------------|-------|
| File > Open Snapshot | forensic | Load timeline from exported snapshot; verifies `hashes.txt` when present and warns before opening unverified or failed bundles. |
| File > Close Snapshot | forensic | Return to live index. |
| File > Export Snapshot | forensic | Export with preflight, manifest, collectionSummary; default stable path. |
| File > Export to SQLite / Open from SQLite | forensic | Stable export/import for timeline. |
| File > Save session now / Restore (on start) | forensic | Session persistence; no live-state alteration. |
| MFT tab: Load from Volumes... | forensic | Raw -> Backup -> VSS; Admin recommended; 100 MB cap. |
| MFT tab: Load MFT file... | forensic | Offline MFT; record size auto-detect. |
| Settings: Forensic Strict / Triage Fast profile | advanced | Profile selection; Triage Fast is non-forensic. |
| Settings: Registry use offline only; Offline Hive | forensic | Forensic-preferred registry path. |
| Settings: Raw Disk (Offline Hive) | advanced | Advanced configuration. |
| Advanced > Registry Search... (when Live enabled) | non-forensic | Live Registry is non-forensic; UI labels it. Use Offline Hive for forensic. |
| Advanced > Registry Search... (when Live disabled) | advanced | Custom offline-only queries only. |
| Advanced > Drill Down | advanced | Navigation from Process to Timeline filter. |
| Advanced > Settings | advanced | All settings (profiles, PID cache, Index cap, etc.). |
| Live Process > Create Dump > minidump / full dump | non-forensic / high-risk | Alters/observes process state; requires Admin for other processes. Not part of default stable forensic workflow. |
| Help > Export diagnostic info | advanced | Diagnostic export. |
| Timeline / Live Stream / Live Process / Live TCP / static tabs (read-only views) | forensic (read-only) or triage | Live views are stable triage baseline; results are not point-in-time consistent. |

**Summary for implementation:**

- **Forensic:** Open/Close/Export Snapshot, SQLite export/open, Save session, Load from Volumes, Load MFT file, Settings (offline-only registry, Offline Hive). These form the default stable forensic path.
- **Non-forensic / high-risk:** Live Registry Search (when enabled), **Create Dump** (minidump/full dump). Must be clearly marked in UI and excluded from the default stable forensic workflow.
- **Advanced:** Settings (profiles, Raw Disk, etc.), Drill Down, Export diagnostic info. Available but not part of the minimal stable path.

---

## 4. High-risk isolation (default stable workflow)

- **Create Dump:** Labeled inline as **Create Dump (High-risk)** in the Live Process context menu, with a ToolTip stating that it is not part of the default forensic workflow. Operators should use it only when authorized by local SOP.
- **Registry Search:** When Live is enabled, UI and preflight already label it as non-forensic. When disabled, only custom offline queries are available; menu text indicates "offline: custom only" / forensic default.
- No high-risk action is required to complete Export Snapshot or to produce a valid manifest with preflight and collectionSummary.

---

**Document update:** 2026-03-19 (Phase 3: operator profile, SOP, UI action classification, high-risk isolation).

