# Cursor Stable Handoff

Use this handoff to continue the stable-update work without re-scoping the project.

## Current status

- Phase 0 is done: stable boundary, roadmap, and agent anti-drift rules are documented.
- Phase 1 is effectively done for the UI/Core local workflow:
  - `manifest.json` records `modeProfile`, `preflight`, and the expanded `collectionSummary`.
  - UI snapshot export runs a real preflight before export.
  - `collectionSummary` now records artifact-copy completeness and UI-side operational counts.
- Phase 2 / Phase 4 automation baseline is now in place:
  - `scripts/InvokeStableReleaseGate.ps1` runs build, tests, and required doc checks.
  - `publish.cmd -StableGate` calls that gate before publish.
  - Release-verification docs and templates now exist.
- Phase 3 (operational consistency) is done:
  - Operator profile and SOP: `docs/StableOperatorProfileAndSOP.md` (Forensic Strict, Triage Fast, default stable workflow, SOP table, UI action classification).
  - High-risk isolation: Create Dump menu items have ToolTip marking them high-risk and not part of default forensic workflow; Registry Search keeps existing non-forensic labeling when Live is enabled.
- Agent hardening is explicitly deferred. Do not pull Agent into the current stable acceptance scope.

## What is not done yet

The main remaining work is manual execution and sign-off:

1. Real execution of the manual verification matrix on target environments.
2. Final stable release sign-off record for an actual release candidate.

## Non-negotiable constraints

- Stable scope is UI/Core local workflow only.
- `HostWitness.Agent` stays out of the current stable release gate.
- New or modified source code and comments must stay ASCII-only.
- If code must keep a Chinese runtime string, use `\uXXXX` escapes in source.
- Do not expand scope into unrelated parser work unless a blocker is found.
- Keep changes aligned with `docs/Agent工作協議.md`.

## Read these files first

Read them in this order:

1. `docs/穩定版邊界定義.md`
2. `docs/穩定版路線圖.md`
3. `docs/Agent工作協議.md`
4. `docs/StableReleaseVerificationMatrix.md`
5. `docs/StableReleaseChecklist.md`
6. `docs/StableReleaseVerificationRecordTemplate.md`
7. `docs/LIMITATIONS.md`
8. `README.md`
9. `publish.cmd`
10. `scripts/InvokeStableReleaseGate.ps1`

Then review these release artifacts before changing anything:

- latest `Release_Verification/stable-release-gate_*.json`
- `docs/StableReleaseVerificationMatrix.md`
- `docs/StableReleaseChecklist.md`
- `docs/StableReleaseVerificationRecordTemplate.md`
- `docs/StableReleaseManualVerificationRunbook.md`
- `docs/StableOperatorProfileAndSOP.md`
- `Release/HostWitness.exe`

## Immediate next task

Execute the remaining manual verification and sign-off workflow only.

The next deliverable should be:

- execution of applicable manual matrix rows on target environments,
- completed verification record for a real release candidate,
- and final stable release sign-off decision with documented blockers (if any).

## Acceptance target for the next slice

- Manual matrix execution is recorded with evidence and explicit `Blocked` handling.
- Verification record and checklist are complete for release owner review.
- Docs, runtime behavior, and stable scope remain aligned.
- `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1` still passes.

## Validation commands

Run these after changes:

```powershell
dotnet build .\WinDFIR.sln -c Release --no-restore -v minimal
dotnet test .\WinDFIR.sln -c Release --no-restore
powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1
```

## Cursor prompt

Use this exact prompt with Cursor if needed:

```text
Continue the HostWitness stable-update work from the current repo state.

Scope:
- Stay inside the UI/Core local stable workflow.
- Do not expand Agent scope.
- Do not rework completed manifest/preflight/collectionSummary/gate work unless you find a concrete bug.

Read first:
1. docs/穩定版邊界定義.md
2. docs/穩定版路線圖.md
3. docs/Agent工作協議.md
4. docs/StableReleaseVerificationMatrix.md
5. docs/StableReleaseChecklist.md
6. docs/StableReleaseVerificationRecordTemplate.md
7. docs/LIMITATIONS.md
8. README.md
9. publish.cmd
10. scripts/InvokeStableReleaseGate.ps1

Then review:
- latest Release_Verification/stable-release-gate_*.json
- docs/StableReleaseVerificationMatrix.md
- docs/StableReleaseChecklist.md
- docs/StableReleaseVerificationRecordTemplate.md
- docs/StableReleaseManualVerificationRunbook.md
- docs/StableOperatorProfileAndSOP.md
- Release/HostWitness.exe

Task:
- Execute the remaining manual verification and sign-off workflow only.
- Record applicable matrix rows with Pass / Fail / Blocked evidence.
- Complete the verification record and checklist for a real release candidate.
- Produce a final stable sign-off decision or list the blockers that prevent sign-off.

Constraints:
- New or modified source code and comments must be ASCII-only.
- If a runtime string must remain Chinese in source, use \uXXXX escapes.
- Keep docs and runtime behavior aligned.
- Do not expand scope back into completed Phase 1 / 3 implementation work unless you find a concrete blocker.
- If you find scope-external issues, list them but do not fix them unless they block this task.

Validation:
- dotnet build .\WinDFIR.sln -c Release --no-restore -v minimal
- dotnet test .\WinDFIR.sln -c Release --no-restore
- powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1
```
