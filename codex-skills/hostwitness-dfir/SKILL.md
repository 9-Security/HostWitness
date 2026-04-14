---
name: hostwitness-dfir
description: HostWitness evidence-analysis workflow for interpreting Windows DFIR artifacts and exported data. Use when Codex reviews HostWitness live views, snapshot bundles, SQLite exports, Agent return artifacts, manifest.json, collect-status JSON, timeline reconstruction, artifact correlation, or incident summaries; prefer this skill for analysis and reporting rather than code changes.
---

# HostWitness DFIR

Treat HostWitness output as evidence with explicit uncertainty. Optimize for defensible interpretation, collection-context awareness, and cross-artifact corroboration.

## Confirm context

1. Confirm the material comes from HostWitness, such as a live UI view, `snapshot_*` bundle, SQLite export, `manifest.json`, `timeline.json`, `collect-status-latest.json`, or HostWitness-generated screenshots/logs.
2. If working from a returned snapshot or Agent collection, read [references/collection-context.md](references/collection-context.md) first.
3. If the question is analytical or investigative, read [references/artifact-correlation.md](references/artifact-correlation.md) before drawing conclusions.
4. If the input is actually a development task, prefer `$hostwitness-dev` instead.

## Core rules

- Separate observed facts from inference.
- Use probabilistic language for conclusions: `consistent with`, `suggests`, `corroborates`, `does not by itself prove`.
- Do not treat absence of evidence as evidence of absence.
- Do not implicitly trust timestamps, process lists, event logs, registry contents, or security-product state.
- Do not call Live Registry forensic-safe; it is explicitly experimental and non-forensic.
- Do not imply perfect point-in-time consistency for live collection or for separate VSS snapshots across volumes.
- Always mention collection caps, dropped events, privilege limits, VSS fallback, and artifact-copy incompleteness when they materially affect interpretation.

## Preferred workflow

1. Establish collection context from `manifest.json`, `collect-status-latest.json`, diagnostic export, or the live-session settings.
2. Verify integrity before analysis when `hashes.txt` or pipeline verification artifacts are available.
3. Normalize time context before correlating events. State whether times are Local or UTC when it matters.
4. Build a sequence from the timeline, then test each important claim against at least one additional source.
5. Prefer offline, snapshot-backed, or verified-bundle evidence over uncorroborated live-only observations.
6. Report findings with explicit caveats, confidence, and recommended follow-up checks.

## What to inspect first

- `modeProfile`
- `registryMode` and `registryLiveEnabled`
- `preflight.executionContext`
- `preflight.isAdministrator`
- `preflight.vssServiceRunning`
- `preflight.useVssSnapshots`
- `preflight.enabledProviders`
- `preflight.warnings` and `preflight.errors`
- `collectionSummary.sourceEventCount`
- `collectionSummary.exportedEventCount`
- `collectionSummary.eventCap` and `wasEventCountCapped`
- `collectionSummary.wasArtifactCopyIncomplete`
- `collectionSummary.skippedEvidenceReferenceCount`
- `collectionSummary.failedEvidenceReferenceCount`
- `collectionSummary.etwDroppedEventTotal`
- `collectionSummary.uiBackpressureDroppedTotal`
- `knownLimitations`

## Output expectations

Prefer this structure when presenting results:

1. Collection context
2. Observed findings
3. Corroborated inferences
4. Caveats and confidence limits
5. Recommended next checks
