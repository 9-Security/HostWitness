# HostWitness File Map

Read this file when the task spans multiple modules or needs project-specific context.

## Repo identity

Treat the current directory as the HostWitness repo only if these anchors exist:

- `WinDFIR.sln`
- `publish.cmd`
- `WinDFIR.Core/`
- `WinDFIR.Providers/`
- `WinDFIR.UI/`
- `WinDFIR.Tests/`

## Canonical docs

- `README.md`: current build, publish, signing, release notes, and top-level scope summary.
- `docs/開發者說明.md`: module ownership, how tabs/views/providers are wired, and which docs must stay in sync.
- `docs/Agent工作協議.md`: anti-drift rules for review, patch, verify, and reporting.
- `docs/穩定版邊界定義.md`: stable baseline, profile boundaries, output boundaries, and high-risk change policy.
- `docs/LIMITATIONS.md`: operational limits, privilege constraints, VSS behavior, export caps, and manifest risk reporting.
- `docs/StableReleaseChecklist.md`: stable release checklist.
- `docs/StableReleaseManualVerificationRunbook.md`: manual stable verification procedure.
- `docs/StableReleaseVerificationMatrix.md`: matrix rows, platform coverage, privilege, and VSS-state validation.
- `docs/StableOperatorProfileAndSOP.md`: operator profiles and high-risk action guidance.

## Code anchors

- `publish.cmd`: authoritative build, test, publish, and signing flow.
- `scripts/InvokeStableReleaseGate.ps1`: automated stable gate and required release-doc markers.
- `WinDFIR.Core/Snapshot/CollectionMetadataBuilder.cs`: `modeProfile`, `registryMode`, and `registryLiveEnabled` logic.
- `WinDFIR.Core/Snapshot/SnapshotExporter.cs`: snapshot bundle export and artifact rewriting.
- `WinDFIR.Core/Snapshot/SnapshotImporter.cs`: snapshot import behavior.
- `WinDFIR.Core/Snapshot/SnapshotIntegrityVerifier.cs`: `hashes.txt` verification and bundle integrity.
- `WinDFIR.Core/Snapshot/SqliteIndexPersistence.cs`: SQLite export/import boundary.
- `WinDFIR.Core/IO/RawDiskReader.cs`: raw disk limits and read behavior.
- `WinDFIR.UI/`: WPF shell, views, view models, and user-visible warnings/status behavior.
- `WinDFIR.Providers/`: collection providers and live/offline acquisition logic.

## Test anchors

- `WinDFIR.Tests/CollectionMetadataBuilderTests.cs`
- `WinDFIR.Tests/PreflightReportBuilderTests.cs`
- `WinDFIR.Tests/SnapshotExporterTests.cs`
- `WinDFIR.Tests/SnapshotSecurityTests.cs`
- `WinDFIR.Tests/PersistenceTests.cs`
- `WinDFIR.Tests/MftAcquisitionRegressionTests.cs`
- `WinDFIR.Tests/RegistrySearchProviderTests.cs`
- `WinDFIR.Tests/ProviderLifecycleHelperTests.cs`

## Common commands

Build:

```powershell
dotnet build .\WinDFIR.sln -c Release --no-restore -v minimal
```

Test:

```powershell
dotnet test .\WinDFIR.sln -c Release --no-restore
```

Build plus test only:

```powershell
cmd.exe /d /c .\publish.cmd -SkipPublish
```

Publish:

```powershell
cmd.exe /d /c .\publish.cmd
```

Stable gate:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1
```

## UI and provider changes

For new or changed tabs, views, or providers, read `docs/開發者說明.md` before patching. Keep `Tag`, `MainViewModel` key arrays, and `ViewRegistryService` registrations consistent.
