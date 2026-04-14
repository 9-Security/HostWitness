Registry Artifact Provider – Specification
15.1 Purpose and Scope

The Registry Provider is designed as a static forensic artifact provider, not a live registry editor or real-time monitoring component.

Its objectives are:

Extract evidentiary registry artifacts from offline registry hive files

Preserve forensic soundness and traceability

Produce correlation-ready outputs for timeline, process, file, and user analysis

Explicitly out of scope:

Live registry modification or editing

Full registry tree browsing (RegEdit-style)

Automated threat scoring or malicious verdicts

15.2 Supported Hive Files (Priority Order)
Priority	Hive	Typical Source
1	SYSTEM	%SystemRoot%\\System32\\config\\SYSTEM
2	SOFTWARE	%SystemRoot%\\System32\\config\\SOFTWARE
3	NTUSER.DAT	User profile root
4	USRCLASS.DAT	UserProfile\\AppData\\Local\\Microsoft\\Windows

All hive files MUST be processed read-only and, when snapshot mode is enabled, copied into the snapshot bundle.

**Implementation (2026-02-02):** Offline Hive is implemented in `OfflineHiveRegistryProvider` with VSS snapshot support (fallback to live paths on failure). Enumerated keys include Services, MountedDevices, AppCompatCache, TimeZoneInformation, ComputerName, Select, Windows, WmiNamespaceSecurity (`ControlSet00x\Control\WMI\Security`); Run/RunOnce/StartupApprovedRun/Winlogon/IFEO/Uninstall/ProfileList/InternetSettingsConnections, BitsClient (`...\CurrentVersion\BITS`), WmiCimom (`...\WBEM\CIMOM`), SrumRegistry (`...\SRUM`); User Run/RunMRU/TypedURLs/RecentDocs/UserAssist/MountPoints2/ComDlg32/OpenSaveMRU/Streams; MuiCache/BagMRU/Bags. Bounded semantics for BITS/WMI/SRUM registry slices: `docs/LIMITATIONS.md` §16. See `docs/待修復問題記錄.md` and `docs/還有什麼要做的事項.md` for the current key list and follow-up items.

15.3 Provider Responsibilities
The Registry Artifact Provider MUST:

Load offline hive files without mounting them into the live registry

Enumerate only configured, high-value registry paths

Extract LastWriteTime for each relevant key

Emit normalized ActivityEvent records

Attach EvidenceRef entries pointing to the source hive and key path

The Registry Artifact Provider MUST NOT:

Modify registry contents

Perform correlation logic

Apply threat scoring or verdicts

15.4 Registry Evidence Categories (M2 Minimum Set)
A. Persistence / Autorun Evidence (High Priority)

Recommended keys:

HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Run

HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce

HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run

HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce

HKLM\\System\\CurrentControlSet\\Services\\*

HKLM\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon

HKLM\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\*

Captured fields:

Hive name

Full key path

Value name

Value data

LastWriteTime

B. User Execution & Interaction Evidence (Medium Priority)

Recommended keys:

HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\RunMRU

HKCU\\Software\\Microsoft\\Internet Explorer\\TypedURLs

Captured fields:

User SID

Key path

Value name and decoded data (where applicable)

LastWriteTime

C. Configuration Change Evidence (Selective)

Examples:

Proxy and Internet settings

Firewall-related configuration

Security policy indicators (read-only)

15.5 Normalization and Mapping Rules

RegistryKey entity identifier MUST be the normalized full registry path

All timestamps MUST be converted to UTC

User context MUST be resolved via SID mapping

File paths extracted from registry values MUST be:

Normalized

Exposed as CandidatePaths[] for correlation with File entities

15.6 ActivityEvent Mapping

Registry artifacts are mapped to ActivityEvent objects with the following constraints:

Category: Registry

Action:

Query (artifact-derived presence)

Set (inferred from LastWriteTime changes)

Subject: User SID or ProcessKey (if correlated externally)

Object: RegistryKey entity

Confidence: Medium (default)

Each ActivityEvent MUST include at least one EvidenceRef.

15.7 EvidenceRef Requirements

Each EvidenceRef MUST include:

Hive file path

Registry key path

LastWriteTime

File offset (if available from the parser)

15.8 UI Consumption Model

The UI MUST:

Treat registry data as evidence tables, not hierarchical trees

Display registry artifacts only in:

Timeline View

Entity Drill-down Panels

Support filtering by:

Hive

User SID

Key path substring

15.9 Correlation Expectations

Registry artifacts are correlated outside the provider with:

File entities (autorun target paths)

Process entities (via EventLog or ETW)

User entities (SID)

The Registry Provider itself performs no joins.

15.10 Definition of Done (Registry Provider)

The Registry Provider is considered complete for M2 when:

Offline hives can be parsed without live registry access

At least 10 high-value registry paths are supported

ActivityEvent output conforms to the unified schema

EvidenceRef traceability is verifiable

Registry artifacts appear correctly in the unified timeline
