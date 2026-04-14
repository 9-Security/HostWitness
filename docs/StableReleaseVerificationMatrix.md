# Stable Release Verification Matrix

This matrix defines the minimum verification set before marking a UI/Core local build as a stable release.

## Scope

- Current scope: UI/Core local stable scope only.
- Out of scope for this matrix: Agent hardening, remote deployment validation, and non-Windows targets.
- Automated checks are handled by `scripts/InvokeStableReleaseGate.ps1`.
- Manual sign-off still requires this matrix plus the checklist and verification record.
- Operator execution workflow is defined in `docs/StableReleaseManualVerificationRunbook.md`.

## Automated Gate

Run the automated release gate before manual sign-off:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1
```

Expected outputs:

- `Release_Verification\stable-release-gate_*.json`
- A passing **`publish.cmd -SkipPublish`** run from **cmd.exe** (restore, build, and tests; avoids PowerShell-hosted `dotnet` SDK resolver noise and uses repo-local **`.nuget-appdata\NuGet`** instead of the user NuGet profile)
- Documentation checks for the stable release docs and manifest coverage notes

## Golden Sample Anchors

These automated anchors should stay green before a stable release:

- MFT exported file load keeps working for 1024-byte and 4096-byte record sizes.
- Snapshot export writes `modeProfile`, `preflight`, and `collectionSummary` into `manifest.json`.
- Snapshot export tracks artifact copy completeness via `copiedArtifactFileCount`, `skippedEvidenceReferenceCount`, `failedEvidenceReferenceCount`, `wasArtifactCopyIncomplete`, and `artifactCopyWarningCount`.
- Snapshot export does not emit a false artifact-copy warning when there are no evidence paths.
- Raw read truncation and related warning paths remain covered by regression tests.

## Manual Verification Matrix

Result values for execution:

- `Pass`: expected behavior observed.
- `Fail`: expected behavior not observed.
- `Blocked`: cannot execute in current environment; reason and owner required.
- `N/A`: explicitly not applicable for this candidate.

| Scenario ID | Platform | Privilege | VSS | File system | What to verify | Result | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M1 | Windows 10 22H2 | Admin | Available | NTFS | Launch UI, collect, export snapshot, verify `preflight` and `collectionSummary` fields in `manifest.json` | Pending | |
| M2 | Windows 11 23H2 | Admin | Unavailable | NTFS | Export snapshot with VSS unavailable and confirm warnings are visible and recorded | Pending | |
| M3 | Windows 11 23H2 | Standard user | Unavailable | NTFS | Export snapshot and confirm non-admin operational warnings are visible and recorded | Pending | |
| M4 | Windows Server 2022 | Admin | Available | NTFS | Confirm stable UI/Core workflow works on server baseline and produces the same manifest metadata | Pending | |
| M5 | Windows 10 or 11 | Admin | Available | Non-NTFS removable media present | Confirm tool remains stable and the release notes still describe NTFS-only raw/MFT expectations | Pending | |
| M6 | Windows 10 or 11 | Admin | Available | NTFS | Use a low-free-space output path and confirm preflight warns before export | Pending | |
| M7 | Windows 10 or 11 | Admin | Available | NTFS | Stress ETW until throttling occurs and confirm `etwTotalDrops` and `etwDroppedEventTotal` are recorded | Pending | |
| M8 | Windows 10 or 11 | Admin | Available | NTFS | Load exported MFT files with both 1024-byte and 4096-byte record sizes and confirm parsing succeeds | Pending | |
| M9 | Windows 10 or 11 | Admin | Available | NTFS | Export snapshot with missing and present evidence paths and confirm artifact copy counts match expectations | Pending | |

## Release Gate

A stable release is blocked until all of the following are true:

- The automated gate passes.
- Applicable matrix rows are signed off for the release candidate.
- README, `docs/README.md`, and `docs/LIMITATIONS.md` match current runtime behavior.
- Known limitations are reviewed for scope drift.
- The verification record is completed and attached to the release decision.
- Any `Blocked` rows include explicit blocker reason, owner, and release decision.
