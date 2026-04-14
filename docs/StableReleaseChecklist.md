# Stable Release Checklist

Use this checklist before marking a UI/Core local build as stable.

## Automated Gate

- [ ] Run `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1`
- [ ] Attach the generated JSON report from `Release_Verification\`
- [ ] Confirm the automated gate passed without skipped required steps
- [ ] Review any failed or skipped checks before continuing

## Manual Verification Matrix

- [ ] Follow `docs/StableReleaseManualVerificationRunbook.md`
- [ ] Run the applicable rows from `docs/StableReleaseVerificationMatrix.md`
- [ ] Record operator, platform, privilege level, and VSS state for each executed row
- [ ] Use result values consistently (`Pass`, `Fail`, `Blocked`, `N/A`)
- [ ] Record deviations, fallbacks, or known-environment constraints
- [ ] For each `Blocked` row, record blocker reason, owner, and release decision
- [ ] Confirm the release candidate stayed within the UI/Core local stable scope

## Known Limitations and Docs

- [ ] Review `docs/LIMITATIONS.md`
- [ ] Confirm README and `docs/README.md` point to the current release gate and verification docs
- [ ] Confirm manifest coverage notes still match runtime behavior for `modeProfile`, `preflight`, and `collectionSummary`
- [ ] Confirm out-of-scope Agent work is not being treated as part of stable acceptance

## Verification Record

- [ ] Fill `docs/StableReleaseVerificationRecordTemplate.md`
- [ ] Link the automated JSON gate report
- [ ] Link manual notes, screenshots, or external run logs as needed
- [ ] Confirm all `Fail` and `Blocked` rows have follow-up actions
- [ ] Store the completed record with the release candidate artifacts

## Approval

- [ ] Final release decision captured
- [ ] Known limitations review captured
- [ ] Release owner sign-off captured
