# Single-Host Live Forensics & Activity Correlation Tool

Document sync note: aligned to the current repository baseline on 2026-03-20. The current stable acceptance scope in this repo is the local UI/Core workflow; the headless Agent remains in the repo but is outside the current stable release gate.

## 1. Purpose and Positioning
This project defines a **single-endpoint live forensic and activity correlation tool** for Windows systems.

The tool is designed for:
- Live response and live observation on **one host at a time**
- Static forensic triage of user and system activity
- High-density, drill-down style analysis inspired by **Sysinternals** tools
- Correlated data views that prevent duplicated logic and fragmented evidence

It is **not** intended to be:
- A multi-endpoint management platform (e.g., EDR, Velociraptor)
- A remediation or response automation tool
- A kernel-driver replacement for Procmon

The tool focuses on **visibility, correlation, and forensic transparency**.

---

## 2. Core Design Principles

1. **Single Data Contract**
   - All collected data must conform to unified entity and event schemas
   - No UI or module may introduce its own private data model

2. **Evidence Traceability**
   - Every derived conclusion must link back to raw evidence
   - Raw artifacts must be preservable and hashable

3. **Non-Invasive Operation**
   - Read-only collection by default
   - No system modification, cleanup, or remediation actions

4. **Live + Snapshot Duality**
   - Support both real-time observation and offline forensic snapshots

5. **UI Replaceability**
   - Core logic must be UI-agnostic
   - UI is a consumer of indexed data, not a data processor

---

## 3. Target Environment

- OS: Windows 10 / Windows 11 (primary baseline); Windows Server is best-effort until the formal stable verification matrix is completed
- Architecture: x64 by default; local ARM64 publish is supported, but is not yet part of the formal stable verification baseline
- Runtime: .NET 8
- Primary Language: C#
- UI Framework (v1): WPF
- Deployment: Portable; self-contained single-file executable by default, with framework-dependent single-file publish optional

---

## 4. High-Level Architecture

```
+--------------------------------------------------+
|                     UI (WPF)                     |
|  Views, Filters, Drill-Down, Timeline, Search    |
+-----------------------↑--------------------------+
|                 Correlation Layer                |
|   Entity Joins, Relationship Resolution           |
+-----------------------↑--------------------------+
|                    Index Layer                   |
|   In-Memory Index + Optional Snapshot / SQLite    |
+-----------------------↑--------------------------+
|                Normalization Layer                |
|   Map Raw Data → Unified Contracts                |
+-----------------------↑--------------------------+
|                  Provider Layer                  |
|   Live Providers + Artifact Providers             |
+-----------------------↑--------------------------+
|                 Raw Evidence Sources              |
+--------------------------------------------------+
```

The current repo also contains a headless Agent that reuses the provider, normalization, index, and snapshot layers for remote collection. It is not part of the current stable release gate.

---

## 5. Core Data Contracts

### 5.1 Global Entity Identifiers

| Entity | Identifier Definition |
|------|-----------------------|
| Host | Machine SID / Hostname |
| User | Windows SID |
| Process | (BootId, PID, CreateTime) |
| File | (VolumeSerial, FileId) or Path+Hash |
| RegistryKey | Normalized Registry Path |
| NetworkFlow | (Proto, LocalEP, RemoteEP, PID, TimeBucket) |

---

### 5.2 ActivityEvent Schema (Central Timeline Unit)

```json
{
  "timestamp": "ISO-8601",
  "category": "Process | File | Network | Registry | Browser | Logon",
  "action": "Start | Stop | Open | Write | Connect | Query",
  "subject": "UserKey or ProcessKey",
  "object": "FileKey | RegKey | FlowKey | URL",
  "summary": "Human-readable description",
  "fields": { "sourceSpecific": "value" },
  "evidence": [
    {
      "source": "EventLog | Prefetch | ETW | LNK | BrowserDB",
      "reference": "Path / RecordId / Offset",
      "hash": "SHA256 (optional)"
    }
  ],
  "confidence": "High | Medium | Low"
}
```

---

## 6. Provider Layer Specification

### 6.1 Live Providers

| Provider | Purpose |
|--------|--------|
| LiveProcessProvider | Process list, tree, command line, user, integrity |
| NetConnectionProvider | TCP/UDP connections mapped to processes |
| ETWMonitorProvider | Subset of Procmon (process/file/registry/network events) |

ETW Scope (v1):
- Process start / stop
- File create / write (subset)
- Registry create / set (subset)
- Network connect (subset)

---

### 6.2 Artifact Providers

| Provider | Evidence Source |
|--------|-----------------|
| EventLogProvider | Security/System/Application EVTX |
| PrefetchProvider | *.pf execution artifacts |
| RecentLnkProvider | Recent folder LNK files |
| JumpListProvider | Automatic & Custom Jump Lists |
| BrowserHistoryProvider | Chromium-based browser SQLite DB |

Each provider outputs **raw records + minimal parsing only**.

---

## 7. Normalization Rules

- All timestamps converted to UTC with original timezone preserved
- Paths normalized (case-insensitive, resolved environment variables)
- ProcessKey generated once and reused across providers
- Users always represented by SID internally
- Evidence objects must retain raw reference (file + offset/record id)

---

## 8. Index & Correlation Layer

### 8.1 Indexing
- In-memory indexes by:
  - ProcessKey
  - UserKey
  - FileKey
  - Time range

### 8.2 Correlation Examples

| Join | Description |
|----|-------------|
| Prefetch → Process | Execution confirmation |
| LNK → File → Process | User interaction leading to execution |
| Browser Download → File | Initial delivery vector |
| NetFlow → Process | Network activity attribution |
| EventLog 4688 → Process | High-confidence process creation |

---

## 9. UI/UX Functional Specification (Sysinternals-Inspired)

### 9.1 Global Capabilities
- Regex / contains search
- Column selection & sorting
- Advanced filter builder (AND / OR)
- Bookmarking & analyst tagging are future extensions and are not part of the current stable baseline

### 9.2 Core Views

| View | Description |
|----|-------------|
| Live Process View | Tree view with drill-down, process-centric filtering, and explicit separation of high-risk dump actions from the default forensic workflow |
| Live TCP / Netstat Views | Live and static network connection inspection with process mapping; diff highlighting is supported when comparing loaded snapshot state against a baseline |
| Timeline View | Unified ActivityEvent timeline |
| Recent Files / Browsing History Views | User-centric interaction history |
| Artifact Views | Prefetch, Amcache, Autorun, Event Log, MFT, System Info, and related static forensic triage tabs |

### 9.3 Drill-Down Rule
Any row → Entity Panel → Related Views

Example:
Process → Network → Prefetch → Recent Files → Browser Download

The current stable UI uses dedicated static tabs instead of a single combined "User Activity View" or "File Artifact View".

---

## 10. Snapshot (Forensic Triage) Mode

### 10.1 Snapshot Output Structure

```
snapshot_bundle/
├─ timeline.json
├─ entities.json
├─ raw/
│  ├─ evtx/
│  ├─ prefetch/
│  ├─ browser/
│  └─ lnk/
├─ manifest.json
└─ hashes.txt
```

### 10.2 Manifest Content
- Tool version and snapshot format version
- Collection time
- Host metadata (`hostname`, `machineSid`, `osVersion`)
- Hashes of bundled files
- Mode and execution metadata (`modeProfile`, `preflight`, `registryMode`, `registryLiveEnabled`)
- Collection summary and integrity/risk metadata (`collectionSummary`, `knownLimitations`, throttling and artifact-copy completeness fields when present)

---

## 11. Live Monitor Mode

- Streaming ETW-backed events
- Pause / resume capture
- Promote live state to snapshot
- Diff highlighting (new process, new connection)

---

## 12. Security & Forensic Considerations

- Default read-only access
- Explicit warning on elevated privileges
- No automatic cleanup
- Output integrity verification supported
- Offline-first forensic defaults are preserved through the `Forensic Strict` profile
- High-risk or non-forensic actions (for example Live Registry and process dump) must be explicitly labeled and kept out of the default forensic workflow

---

## 13. Development Milestones

### M1 – Core Platform
- Data contracts
- Process + Network live views
- Index & correlation engine

### M2 – Artifact Correlation
- EventLog, Prefetch, Recent, Browser history, Amcache, Autorun
- Offline Hive, Snapshot, SQLite export/open, and MFT forensic triage flows
- Timeline view
- Entity drill-down

### M3 – Procmon Subset
- ETW monitor integration
- Live → snapshot merge
- Baseline diff highlighting for new processes and connections

---

## 14. Future Extensions (Out of Scope for v1)

- Memory dump ingestion (Volatility integration)
- Bookmarking & analyst tagging
- Dedicated composite "User Activity View" beyond the current specialized tabs
- Dedicated composite "File Artifact View" beyond the current specialized tabs
- Plugin SDK
- WinUI 3 UI frontend

---

## 15. Summary

This specification defines a **single-host, professional-grade live forensic analysis tool** that:
- Preserves Sysinternals philosophy
- Avoids duplicated logic via unified data contracts
- Supports both live observation and forensic triage
- Is engineered for extensibility and evidence integrity

This document serves as the **authoritative development reference** for implementation.

---

## Appendix A. 程式碼檢查與建議 (2026-01-28 初版，2026-03-20 同步現況)

以下附錄保留 2026-01-28 的程式碼檢查脈絡，但已同步目前 repo 現況，避免把已修正項目誤記為仍存在的落差。

### A.1 已於後續輪次完成的主要修正
- **事件動作(Action)正規化**  
  現行實作已加入 `ActivityEventNormalizer`，將 `Create / Set / Execute / Listen / Disconnect / Visit / Failed` 等較寬鬆來源動作正規化回規格基線（`Start | Stop | Open | Write | Connect | Query`），並在需要時保留 `OriginalAction` 供追溯。

- **InMemoryActivityIndex 併發安全與容量控制**  
  現行索引改用 `ConcurrentQueue` / `ConcurrentDictionary`，並加入 `MaxEvents`、eviction 與 queue trim 流程，不再是早期 `ConcurrentDictionary<, List<ActivityEvent>>` 的 thread-safety 風險模型。

- **NetworkFlow 與 PID 關聯**  
  `NetConnectionProvider` 現行已使用 `GetExtendedTcpTable / GetExtendedUdpTable` 取得 PID 對應，NetworkFlow 不再是「無法取得 PID」的舊狀態。

- **Snapshot 原始證據匯出與可攜性**  
  `SnapshotExporter` 現行會嘗試解析可攜式原始證據路徑、複製至 bundle `raw/`、改寫 evidence reference，並把完整度與 warning 寫入 `collectionSummary`；不再是早期「常因 `File.Exists(...)` 直接失敗而無法完整匯出」的狀態。

- **Machine SID 可信度**  
  `manifest.json` 的 `machineSid` 現行改為以本機 Administrator SID 前綴推導，必要時回退 `MachineGuid`，不再使用 `MachineName.GetHashCode()` 這類不可辯護的假值。

- **ETW / BootId 基線**  
  目前已不是早期自訂 `EventSource` 示意模型；現行 `ETWMonitorProvider` 使用 ETW-backed event channels，`BootIdProvider` 也改為以系統 uptime 推導並保留 fallback。它仍是 Procmon subset，而非完整 kernel-driver replacement。

### A.2 目前仍值得保留的行為正確性 / 鑑識可信度議題
- **EventLogProvider 結構化欄位解析仍可加強**  
  目前仍有空間針對 4688 / 4624 等高價值事件做更完整的 XML 欄位 mapping，以提升 PID、SID、CommandLine 的結構化品質。

- **LiveProcessProvider 的使用者資訊仍偏簡化**  
  目前 `GetProcessUserName` 仍偏向簡化路徑，若要提升準確度，後續可改用 WMI `Win32_Process.GetOwner` 或 Token API。

- **時間正規化仍有 Unspecified DateTime 假設**  
  `TimeNormalizer` 對 `DateTimeKind.Unspecified` 仍以 UTC 處理；對本地時間來源（如 LNK / JumpList / Prefetch）若要更嚴謹，後續仍可再補來源感知的時區推論。

- **live triage 本質上仍非 point-in-time consistent**  
  即使目前 ETW、Live Process、Live TCP 已能穩定使用，live 視角仍受主機當下狀態、權限、ETW throttling 與 API hook 影響；這是產品邊界，不是單一 bug。

### A.3 性能與可維護性建議
- **重複雜湊計算仍可持續整理**  
  某些 provider 仍可能對同一檔案重複計算 SHA256；若後續需要再做大批量效能優化，可考慮快取或單次計算。

- **EventLogProvider 的屬性遍歷效能仍可微調**  
  若後續要進一步壓低 large-log 掃描成本，可持續整理屬性遍歷與 mapping 流程。

---

以上建議可視需求分為「必修/優化/未來改善」三級處理。若需要，我可以依照優先級直接協助修正程式碼。

---

## Appendix B. ETW 欄位對照與正規化建議 (v1)

為降低不同事件版本或通道帶來的欄位差異，下列欄位名稱建議視為同義並統一處理：

### B.1 檔案路徑
- `FileName`
- `FilePath`
- `TargetFilename`
- `Path`

### B.2 Registry 路徑
- `KeyName`
- `ObjectName`
- `RegistryPath`
- `KeyPath`

### B.3 PID
- `Execution/ProcessID` (Event XML)
- `ProcessId`
- `ProcessID`
- `PID`

### B.4 網路端點
- Local Address: `LocalAddress`, `SourceAddress`, `saddr`
- Remote Address: `RemoteAddress`, `DestAddress`, `daddr`
- Local Port: `LocalPort`, `SourcePort`, `sport`
- Remote Port: `RemotePort`, `DestPort`, `dport`
- Protocol: `Protocol`, `ProtocolName`

### B.5 端點格式正規化
- IPv4: `IP:Port` (例：`10.0.0.1:443`)
- IPv6: `[IP]:Port` (例：`[2001:db8::1]:443`)

以上欄位名稱需視實際 ETW provider 版本調整，但建議至少支援以上集合以避免映射失敗。
