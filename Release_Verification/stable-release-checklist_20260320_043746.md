# Stable Release Checklist

Candidate: `Release/HostWitness.exe` 1.0.1  
Date UTC: 2026-03-20T04:37:46Z  
Operator: Codex workspace automation  
Gate report: `Release_Verification/stable-release-gate_20260320_043746.json`

## Automated Gate

- [x] Run `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1`
- [x] Attach the generated JSON report from `Release_Verification\`
- [x] Confirm the automated gate passed without skipped required steps
- [x] Review any failed or skipped checks before continuing

Notes:
- Gate result: PASS
- Build: PASS
- Tests: PASS (`80/80`)
- Docs: PASS

## Manual Verification Matrix

- [x] Follow `docs/StableReleaseManualVerificationRunbook.md`
- [x] Run the applicable rows from `docs/StableReleaseVerificationMatrix.md`
- [x] Record operator, platform, privilege level, and VSS state for each executed row
- [x] Use result values consistently (`Pass`, `Fail`, `Blocked`, `N/A`)
- [x] Record deviations, fallbacks, or known-environment constraints
- [x] For each `Blocked` row, record blocker reason, owner, and release decision
- [x] Confirm the release candidate stayed within the UI/Core local stable scope

Notes:
- Matrix record stored in `Release_Verification/stable-release-verification-record_20260320_043746.md`
- M8 and M9 were satisfied by direct regression evidence in the candidate test run
- M1-M7 remain `Blocked`, but the release owner waived manual matrix execution for this candidate and accepted automated gate + targeted regression coverage only

## Known Limitations and Docs

- [x] Review `docs/LIMITATIONS.md`
- [x] Confirm README and `docs/README.md` point to the current release gate and verification docs
- [x] Confirm manifest coverage notes still match runtime behavior for `modeProfile`, `preflight`, and `collectionSummary`
- [x] Confirm out-of-scope Agent work is not being treated as part of stable acceptance

Notes:
- Current documentation and runtime scope were re-synchronized before this checklist run
- Agent remains out of the current stable release gate

## Verification Record

- [x] Fill `docs/StableReleaseVerificationRecordTemplate.md`
- [x] Link the automated JSON gate report
- [x] Link manual notes, screenshots, or external run logs as needed
- [x] Confirm all `Fail` and `Blocked` rows have follow-up actions
- [x] Store the completed record with the release candidate artifacts

Notes:
- Completed record: `Release_Verification/stable-release-verification-record_20260320_043746.md`
- Sign-off note: `Release_Verification/stable-release-signoff_20260320_043746.md`
- No screenshots were captured in this non-interactive terminal run; blocked rows explicitly record that limitation and the release-owner waiver

## Approval

- [x] Final release decision captured
- [x] Known limitations review captured
- [x] Release owner sign-off captured

Decision summary:
- Release owner: Sharlotlot
- Decision: Approved for stable release with manual matrix waiver in this workspace run
- Reason: automated gate passed; M8/M9 have direct regression evidence; release owner explicitly waived manual matrix execution for blocked rows M1-M7
