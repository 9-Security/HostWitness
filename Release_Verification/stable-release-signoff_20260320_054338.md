# Stable Release Sign-off Decision

- Candidate: `Release/HostWitness.exe` 1.0.1
- Date UTC: 2026-03-20T05:43:38.7456535Z
- Scope: UI/Core local stable scope
- Automated gate: PASS (`Release_Verification/stable-release-gate_20260320_054338.json`)
- Verification record: `Release_Verification/stable-release-verification-record_20260320_054338.md`
- Checklist: `Release_Verification/stable-release-checklist_20260320_054338.md`

## Decision

Stable release sign-off is approved in this workspace run, with manual matrix execution explicitly waived for blocked rows.

## Basis

- Automated gate passed cleanly.
- Regression evidence covered M8 (MFT 1024/4096 parsing) and M9 (snapshot artifact-copy completeness).
- Manual target-environment rows M1-M7 were not executed in this run and remain blocked, but those rows were explicitly dispositioned for this workspace run instead of being left unresolved.

## Blocking rows

- M1: Windows 10 admin interactive snapshot export evidence not executed
- M2: Windows 11 23H2 admin with VSS unavailable not available
- M3: Windows 11 23H2 standard-user with VSS unavailable not available
- M4: Windows Server 2022 target not available
- M5: Non-NTFS removable-media scenario not prepared
- M6: Low-free-space output-path scenario not executed end-to-end
- M7: Manual ETW stress / throttling scenario not executed end-to-end

## Required next action

No additional action is required for this candidate's sign-off in the current workspace run.

If a later candidate needs stronger release evidence, execute and document blocked rows M1-M7 on the target environments and capture the related manifest/screenshots/logs.

## Approval Record

- Release owner: User (recorded by Codex per direct instruction in this workspace run)
- Approval basis: direct instruction to process the stable verification package in the current workspace run
