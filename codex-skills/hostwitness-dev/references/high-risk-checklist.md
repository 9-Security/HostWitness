# HostWitness High-Risk Checklist

Read this file before patching any release-sensitive or forensic-sensitive surface.

## Treat these requests as high risk

- Change `manifest.json` fields or semantics.
- Change snapshot bundle files, `hashes.txt`, artifact rewriting, or snapshot integrity behavior.
- Change SQLite schema, persistence format, or migration behavior.
- Change `Forensic Strict` or `Triage Fast` defaults or profile detection.
- Change privilege, VSS, raw disk, Backup privilege, live fallback, or MFT acquisition order.
- Change Live Registry or process-dump behavior.
- Change release gate, publish flow, signing behavior, or stable acceptance language.

## Read before patching

- `docs/穩定版邊界定義.md`
- `docs/LIMITATIONS.md`
- `docs/Agent工作協議.md`
- `publish.cmd`
- `scripts/InvokeStableReleaseGate.ps1`
- `WinDFIR.Core/Snapshot/CollectionMetadataBuilder.cs`
- the nearest relevant tests under `WinDFIR.Tests/`

## Preserve these invariants

- Keep `Forensic Strict` offline-first and do not silently default-enable Live Registry.
- Keep `Triage Fast` labeled as triage-first, not forensic-safe.
- Keep snapshot manifest risk-reporting fields consistent: `modeProfile`, `preflight`, `collectionSummary`, `registryMode`, `registryLiveEnabled`, `knownLimitations`, and drop counters.
- Keep snapshot evidence contained to the bundle `raw/` directory.
- Keep raw disk and MFT read-cap warnings visible when truncation occurs.
- Keep VSS/admin limitations explicit and user-visible.
- Keep artifact-copy completeness and skipped-reference reporting explicit.

## Minimum verification

- Run `dotnet build .\WinDFIR.sln -c Release --no-restore -v minimal`.
- Run `dotnet test .\WinDFIR.sln -c Release --no-restore`.
- Run targeted regression tests if the change is concentrated in one area.
- Run `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1` when release flow, acceptance criteria, or release docs changed.
- State clearly when manual verification, admin-only flows, or real VSS validation were not run.

## Docs to sync when behavior changes

- `README.md`
- `docs/README.md`
- `docs/開發者說明.md`
- `docs/LIMITATIONS.md`
- `docs/穩定版邊界定義.md`
- `docs/StableReleaseChecklist.md`
- `docs/StableReleaseManualVerificationRunbook.md`
- `docs/StableReleaseVerificationMatrix.md`
- `docs/StableReleaseVerificationRecordTemplate.md`
- `docs/StableOperatorProfileAndSOP.md`
