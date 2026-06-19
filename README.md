# HostWitness

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)

**Open-source Windows live-forensics and activity-correlation tool for single-host triage.** Maintained by **nine-security Inc.** (author: Shar).

> 🌏 **中文說明：[README.zh-TW.md](README.zh-TW.md)**

HostWitness collects and correlates live and offline forensic artifacts on a single Windows host — event logs, registry hives, MFT, Prefetch, Jump Lists, Amcache, SRUM, browser history, running processes, network connections, and persistence locations — into one unified, filterable timeline. Finished investigations export as a portable, SHA-256 integrity-hashed snapshot for defensible incident response.

## Features

- **Unified timeline** correlating live and offline artifacts, with filtering, time-range export (CSV/JSON), and time-zone display (Local/UTC).
- **Live process & TCP views** (Procmon / TCPView style) with rich columns (parent, owner SID, integrity, image hash, Authenticode) and **drill-down** to a process's related file/registry/network events.
- **Offline registry hive parsing** (SYSTEM / SOFTWARE / NTUSER / USRCLASS) including UserAssist, ShimCache/AppCompatCache, Amcache, and persistence keys; conservative coverage of BITS / WMI / SRUM subtrees.
- **MFT** from live volumes (raw volume → backup privilege → VSS fallback) or an exported `$MFT`; **Prefetch** (versions 10–31, incl. Windows 11); **Jump List / DestList**; **Recent LNK**.
- **Event log collection** including common IR operational channels (PowerShell, WMI-Activity, Task Scheduler, Defender, Sysmon) when available.
- **Process memory dump** (minidump / full dump).
- **Snapshot export** with bundle-local evidence (`raw/`) and a `hashes.txt` integrity manifest, so bundles are portable and independently verifiable.
- **Forensic-first defaults** (offline hive, strict profile); live registry is experimental and opt-in.
- **Optional remote collection agent** (`HostWitness.Agent`) — see the docs.

## Download & verify

Released binaries are **not EV-signed yet**, so Windows SmartScreen will show an "unknown publisher" warning — this is expected and does not mean the file is unsafe. **Always verify the SHA-256 before running:**

```powershell
Get-FileHash .\HostWitness.exe -Algorithm SHA256
```

Compare the result against the hash published in the matching `docs/RELEASE_NOTES_<version>.md` and on the GitHub Release page. Full guide: **[docs/VERIFY_AND_SMARTSCREEN.md](docs/VERIFY_AND_SMARTSCREEN.md)**.

## Build from source

Requires the **.NET 8 SDK** and the **Windows SDK** (for `signtool`, optional). The most reliable trust path is to build it yourself — the code is auditable and the build is reproducible.

```bat
git clone https://github.com/9-Security/HostWitness
cd HostWitness
cmd.exe /d /c .\publish.cmd
```

This produces `Release\HostWitness.exe` (self-contained, single-file, win-x64). Common variants:

```bat
cmd.exe /d /c .\publish.cmd -Runtime win-arm64     :: Windows on ARM64
cmd.exe /d /c .\publish.cmd -FrameworkDependent     :: smaller; needs .NET 8 Desktop Runtime on target
cmd.exe /d /c .\publish.cmd -SkipPublish            :: build + test only
```

Build, publish, signing and release-checklist details: **[docs/建置與發布.md](docs/建置與發布.md)**.

## Requirements

- Windows 10 / 11, x64 (or ARM64).
- **Administrator privileges** are required for raw-disk / MFT, VSS snapshots, memory dumps, and live registry features. Many read-only views work without elevation.

## Documentation

| Topic | Document |
| --- | --- |
| Usage guide (中文) | [docs/使用說明.md](docs/使用說明.md) |
| Download verification & SmartScreen | [docs/VERIFY_AND_SMARTSCREEN.md](docs/VERIFY_AND_SMARTSCREEN.md) |
| Limitations & forensic assumptions | [docs/LIMITATIONS.md](docs/LIMITATIONS.md) · [docs/FORENSIC_ASSUMPTIONS.md](docs/FORENSIC_ASSUMPTIONS.md) |
| Architecture | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Developer guide (中文) | [docs/開發者說明.md](docs/開發者說明.md) |
| Build & release | [docs/建置與發布.md](docs/建置與發布.md) |
| Feature history / changelog (中文) | [docs/變更摘要.md](docs/變更摘要.md) |
| Remote collection agent (中文) | [docs/遠端採集Agent說明.md](docs/遠端採集Agent說明.md) |

## Project layout

```
HostWitness/
├── WinDFIR.Core/        # Core contracts: entity keys, normalization, index, snapshot
├── WinDFIR.Providers/   # Artifact providers (event log, registry, MFT, browser, ...)
├── WinDFIR.UI/          # WPF application
├── WinDFIR.Agent/       # Optional remote collection agent
├── WinDFIR.Tests/       # Unit tests
├── publish.cmd          # Official build / test / publish entrypoint
└── docs/                # Documentation
```

## License

Licensed under the **Apache License 2.0** — see [LICENSE](LICENSE). © 2026 nine-security Inc.

## Disclaimer

HostWitness is a user-mode live-forensics tool. Use it only on systems you are authorized to investigate. Running any live tool perturbs system state, and a sufficiently privileged kernel/firmware implant can deceive user-mode collection — treat anomalies as leads to corroborate, not as proof. See [docs/LIMITATIONS.md](docs/LIMITATIONS.md).
