---
name: hostwitness-dev
description: HostWitness repo workflow and guardrails for maintaining the Windows DFIR desktop tool. Use when Codex works in the HostWitness or WinDFIR codebase on WPF UI, providers, snapshot or SQLite export, manifest fields, VSS/raw disk/backup privilege paths, Forensic Strict or Triage Fast behavior, release gate and publish flow, or repo documentation synchronization.
---

# HostWitness Dev

Work as a repo-specific maintainer for HostWitness. Optimize for stable DFIR behavior, bounded scope, and release safety rather than opportunistic cleanup.

## Confirm context

1. Confirm the repo root contains `WinDFIR.sln`, `publish.cmd`, `WinDFIR.Core/`, `WinDFIR.UI/`, and `WinDFIR.Tests/`.
2. If the task is repo-wide or unfamiliar, read `README.md`, `docs/開發者說明.md`, and `docs/Agent工作協議.md` first.
3. If the task touches release, outputs, profiles, or acquisition behavior, also read [references/high-risk-checklist.md](references/high-risk-checklist.md).
4. If those files are missing or the repo is not HostWitness, say the skill only partially applies and fall back carefully.

## Default operating mode

- Keep scope tight. Do not clean unrelated warnings, dead code, or docs unless the task requires it.
- Treat the following as high risk: `manifest.json` structure, snapshot bundle semantics, SQLite schema, acquisition flow, privilege behavior, VSS/raw/backup fallbacks, `Forensic Strict` or `Triage Fast` defaults, and release-gate docs.
- For high-risk work, begin with read-only review or a short plan before patching unless the user clearly asks for direct implementation.
- Treat warning text, truncation notices, privilege checks, and "non-forensic" labeling as product behavior, not cosmetic copy.

## File map

Read [references/file-map.md](references/file-map.md) for module ownership, canonical docs, code anchors, and common commands.

## Workflow

1. Read the nearest implementation and tests before editing.
2. Patch the smallest coherent surface that solves the task.
3. Add or update tests for behavior changes, especially export/import/security logic.
4. Run the narrowest meaningful verification first; run solution build/test for non-trivial changes.
5. Sync docs whenever behavior, outputs, defaults, or release steps change.

## Release and verification

- Build, test, publish, and signing behavior are driven by `publish.cmd`.
- Use `cmd.exe /d /c .\publish.cmd -SkipPublish` for build and test only.
- Use `cmd.exe /d /c .\publish.cmd` for normal publish.
- Use `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1` for the automated stable gate.
- Stable gate writes JSON reports to `Release_Verification\` and does not replace the manual matrix and checklist sign-off.

## Non-negotiable invariants

- Keep the stable release scope centered on the local UI/Core workflow; `WinDFIR.Agent` is not part of current stable acceptance unless the user explicitly asks to work on it.
- Preserve the MFT and raw acquisition fallback order unless the user explicitly requests a behavior change: raw volume -> Backup privilege direct `$MFT` -> VSS snapshot.
- Preserve snapshot bundle boundaries: `timeline.json`, `entities.json`, `manifest.json`, `hashes.txt`, and `raw/`.
- Do not silently change manifest field names or semantics such as `modeProfile`, `preflight`, `collectionSummary`, `registryMode`, `registryLiveEnabled`, `knownLimitations`, or drop counters.
- Do not change `Forensic Strict` to default-enable Live Registry. `Triage Fast` is triage-first and not forensic-safe.
- Preserve documented caps and warnings such as event export limits, 100 MB raw-read limits, ETW throttling visibility, UI backpressure visibility, and artifact copy completeness reporting.

## Documentation sync

When behavior changes, update the smallest relevant set of docs. Start with:

- `README.md`
- `docs/README.md`
- `docs/開發者說明.md`
- `docs/LIMITATIONS.md`
- `docs/穩定版邊界定義.md`
- `docs/StableRelease*.md` when release-gate or acceptance behavior changes

## Output expectations

Report:

- what changed
- what was verified
- any scope-out findings not addressed
- any release or forensic risk that remains
