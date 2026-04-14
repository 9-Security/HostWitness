# Stable Release Verification Record

- Release candidate: `Release/HostWitness.exe` 1.0.1
- Candidate commit/tag: Unavailable in current workspace (`.git` directory not present)
- Date UTC: 2026-03-20T04:37:46Z
- Operator: Codex workspace automation
- Scope: UI/Core local stable scope
- Automated gate report: `Release_Verification/stable-release-gate_20260320_043746.json`
- Manual runbook used: `docs/StableReleaseManualVerificationRunbook.md`

## Automated Gate

- Command: `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1`
- Result: PASS
- JSON report path: `Release_Verification/stable-release-gate_20260320_043746.json`
- Notes:
  - Build PASS
  - Tests PASS (`80/80`)
  - Docs PASS
  - `releaseScope = ui_core_local`

## Manual Verification Matrix

| Scenario ID | Environment | Result (Pass/Fail/Blocked/N/A) | Evidence | Notes |
| --- | --- | --- | --- | --- |
| M1 | Current workspace host: Windows 10 Pro 25H2 (`10.0.26200`), standard user, VSS service running | Blocked | `HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion`; admin check = `False`; `Get-Service VSS`; gate report PASS | Row requires Windows 10 admin interactive UI export with snapshot inspection. Current run is a non-admin terminal session without interactive UI execution evidence. Automated manifest field coverage is green, but the required admin UI path was not executed in this run. |
| M2 | Required env not available: Windows 11 23H2, admin, VSS unavailable | Blocked | Current host is Windows 10 Pro 25H2; no Windows 11 23H2 admin target with VSS disabled was available in this workspace run | Manual target environment not available. |
| M3 | Required env not available: Windows 11 23H2, standard user, VSS unavailable | Blocked | Current host is Windows 10 Pro 25H2; VSS service is running; no Windows 11 23H2 target prepared | Manual target environment not available. |
| M4 | Required env not available: Windows Server 2022, admin, VSS available | Blocked | No Windows Server 2022 target host available in this workspace run | Manual target environment not available. |
| M5 | Required env not available: Windows 10/11 host with non-NTFS removable media present | Blocked | No prepared non-NTFS removable media evidence in this workspace run; volume enumeration was not available without elevated CIM access | Environment condition not reproduced in current run. |
| M6 | Current workspace host only; no dedicated low-free-space output target prepared | Blocked | `WinDFIR.Tests/PreflightReportBuilderTests.cs` covers low-free-space warning behavior; gate report PASS | Automated component coverage exists for low-free-space preflight warnings, but the manual end-to-end row was not executed against a real constrained output path in this run. |
| M7 | Current workspace host only; no interactive ETW stress run captured | Blocked | `WinDFIR.Tests/SnapshotExporterTests.cs` covers `etwDroppedEventTotal` serialization; gate report PASS | Field plumbing is covered by tests, but no manual stress run was executed to confirm live ETW throttling warnings and manifest recording in this candidate run. |
| M8 | Automated regression coverage in current candidate test run | Pass | `WinDFIR.Tests/MftParserRegressionTests.cs` (`MftParser_DetectRecordSize_Prefers4096ByteRecords` plus 1024-byte parse coverage); `WinDFIR.Tests/MftAcquisitionRegressionTests.cs`; gate report PASS | This row is satisfied by current regression coverage for 1024-byte and 4096-byte MFT parsing on the release candidate. No additional interactive UI evidence was captured. |
| M9 | Automated regression coverage in current candidate test run | Pass | `WinDFIR.Tests/SnapshotExporterTests.cs` (`ExportAsync_RewritesCopiedEvidenceReferences_ToSnapshotLocalRawPaths`, `ExportAsync_Manifest_TracksArtifactCopyCounts`, `ExportAsync_Manifest_DoesNotReportArtifactCopyWarning_WhenNoEvidencePaths`); gate report PASS | Snapshot artifact-copy completeness and reference rewriting are directly covered by current regression tests and passed in the candidate test run. |

## Blockers and Follow-up

| Scenario ID | Blocker reason | Unblock requirement | Owner | Target date | Release decision |
| --- | --- | --- | --- | --- | --- |
| M1 | No admin interactive UI run was executed for snapshot export on a Windows 10 target | Run HostWitness as administrator on a Windows 10 target, export snapshot, and attach manifest evidence/screenshots | Sharlotlot | Waived for this candidate | Release owner waived manual matrix execution for this candidate; accept automated gate + targeted regression coverage only |
| M2 | No Windows 11 23H2 admin target with VSS unavailable was available | Prepare a Windows 11 23H2 admin target, disable/unset VSS availability for the scenario, execute row, attach evidence | Sharlotlot | Waived for this candidate | Release owner waived manual matrix execution for this candidate; accept automated gate + targeted regression coverage only |
| M3 | No Windows 11 23H2 standard-user target with VSS unavailable was available | Prepare a Windows 11 23H2 standard-user target, execute row, attach evidence | Sharlotlot | Waived for this candidate | Release owner waived manual matrix execution for this candidate; accept automated gate + targeted regression coverage only |
| M4 | No Windows Server 2022 target was available | Execute the stable UI/Core workflow on Windows Server 2022 and record manifest evidence | Sharlotlot | Waived for this candidate | Release owner waived manual matrix execution for this candidate; accept automated gate + targeted regression coverage only |
| M5 | No non-NTFS removable-media scenario was prepared | Provide a Windows 10/11 target with non-NTFS removable media present and execute the row | Sharlotlot | Waived for this candidate | Release owner waived manual matrix execution for this candidate; accept automated gate + targeted regression coverage only |
| M6 | No real low-free-space output-path run was executed | Prepare a low-free-space target/output path, run export, and capture preflight warning evidence | Sharlotlot | Waived for this candidate | Release owner waived manual matrix execution for this candidate; accept automated gate + targeted regression coverage only |
| M7 | No manual ETW stress scenario was executed | Generate a reproducible ETW stress scenario, confirm live warning behavior, and attach manifest evidence | Sharlotlot | Waived for this candidate | Release owner waived manual matrix execution for this candidate; accept automated gate + targeted regression coverage only |

## Known Limitations Review

- Reviewed `docs/LIMITATIONS.md`: Yes
- Scope drift found: None in the UI/Core local stable scope during this run
- Follow-up required:
  - Manual matrix rows M1-M7 were not executed in this candidate run and were explicitly waived by release owner decision
  - Keep Agent out of stable acceptance until its separate hardening/verification work is scheduled

## Approval

- Release owner: Sharlotlot
- Decision: Approved for stable release with manual matrix waiver in this workspace run
- Follow-up items:
  - If a future candidate requires stronger release evidence, execute M1-M7 on the target environments and attach screenshots / manifest samples / external run logs
  - Keep the waiver explicit in any release notes or internal handoff that reference this candidate
