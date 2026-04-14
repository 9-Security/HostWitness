# Stable Release Manual Verification Runbook

This runbook defines how a release operator executes and records manual verification for the UI/Core local stable scope.

## Scope and policy

- Scope is UI/Core local workflow only.
- Agent hardening and remote workflows are out of scope.
- Do not fabricate results.
- If a scenario cannot be executed in the current environment, mark it as `Blocked` and record the reason.

## Inputs and required artifacts

- Candidate build identifier (commit, package, or version tag).
- Automated gate JSON report from `Release_Verification\stable-release-gate_*.json`.
- Target environment details for each executed matrix row.
- Snapshot output artifacts needed for manifest checks (for rows that export snapshots).

## Pre-run steps

1. Run automated gate:
   - `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1`
2. Confirm gate result is PASS.
3. Open:
   - `docs/StableReleaseVerificationMatrix.md`
   - `docs/StableReleaseChecklist.md`
   - `docs/StableReleaseVerificationRecordTemplate.md`
4. Fill basic record metadata first (candidate, date, operator, gate report path).

## Manual matrix execution workflow

For each applicable scenario row in `docs/StableReleaseVerificationMatrix.md`:

1. Prepare environment exactly as required (platform, privilege, VSS state, file system condition).
2. Execute the scenario steps.
3. Capture evidence:
   - Key screenshots (when UI warnings/behavior are part of verification).
   - Output file paths (especially snapshot `manifest.json` checks).
   - Relevant notes (fallbacks, warnings, deviations).
4. Record result in verification record:
   - `Pass` if expected behavior is observed.
   - `Fail` if expected behavior is not observed.
   - `Blocked` if environment/setup prevents execution.
   - `N/A` only when scenario is explicitly not applicable for this release candidate.
5. If `Fail` or `Blocked`, add explicit follow-up action and owner.

## Blocked scenario handling

Use `Blocked` when execution is impossible in current environment, for example:

- Required OS baseline unavailable (for example, no Windows Server 2022 host for M4).
- Required privilege/service state cannot be safely established.
- Hardware or file-system condition cannot be reproduced.

Required record fields for each blocked row:

- Blocker reason (specific and testable).
- Required environment or prerequisite to unblock.
- Owner.
- Target date or release decision (defer/waive/retest).

## Minimum sign-off criteria

Stable sign-off is allowed only when all conditions are met:

- Automated gate PASS.
- Applicable matrix scenarios are resolved as `Pass`, or explicitly dispositioned as `Blocked` with owner and decision.
- Verification record is complete.
- Checklist is complete.
- Release owner decision is captured.

## Output package for release decision

Attach these items to the release decision:

- Gate JSON report path.
- Completed verification record.
- Completed checklist.
- Supporting notes/screenshots/logs for manual rows.
