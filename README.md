# HostWitness — Beta Release

**HostWitness** is a Windows single-host live forensics and activity correlation tool. It collects and correlates processes, network connections, timeline events, and static artifacts on one machine.

This public repository contains **usage documentation only**. **Source code is not published here.** Binaries are distributed as a **zip** on **[GitHub Releases](https://github.com/9-Security/HostWitness/releases)**.

---

## System Requirements

- **OS**: Windows 10/11 (x64; see release notes for ARM64 builds if offered)
- **Privileges**: Most features work with normal user rights; **some require Administrator** (e.g. VSS snapshots, Offline Hive, Create Dump for other processes)
- **.NET**: The distributed executable is **self-contained**; you do **not** need to install .NET on the target machine

---

## Download and Verification

**Download the zip** from the [Releases](https://github.com/9-Security/HostWitness/releases) page. Each release typically includes:

- **HostWitness-beta-*.zip** — archive containing **HostWitness.exe** (self-contained)
- **A file whose filename is the SHA-256 of HostWitness.exe** (64 hex chars + `.txt`) — integrity verification sidecar

Extract the zip to get **HostWitness.exe**, then verify it matches the hash file in Releases.

### How to verify integrity

After downloading the zip and the hash-named file from Releases, extract the zip, then:

**Linux / WSL:**
```bash
sha256sum HostWitness.exe
```
Compare the output with the **filename** of the hash file in Releases (without `.txt`). They must match.

**Windows PowerShell:**
```powershell
(Get-FileHash -Algorithm SHA256 .\HostWitness.exe).Hash
```
Compare with the hash file’s **filename** (without `.txt`). They must match.

---

## How to Use

1. **Unzip** (if you downloaded the zip): Extract **HostWitness-beta-*.zip** to any folder.
2. **Run**: Double-click **HostWitness.exe**, or from PowerShell/Command Prompt:
   ```powershell
   .\HostWitness.exe
   ```
3. **Run as Administrator** (recommended for VSS, Offline Hive, or dumping other processes):  
   Right-click HostWitness.exe → **Run as administrator**, or:
   ```powershell
   Start-Process -FilePath ".\HostWitness.exe" -Verb RunAs
   ```
4. **Settings and logs**:
   - Settings: `%AppData%\HostWitness\settings.json`
   - Startup error log: `%AppData%\HostWitness\logs\startup.log`

For detailed usage, see **USAGE.md** in this folder.

---

## Features (summary)

- **Dynamic analysis**: Timeline, Live Stream, Live Process (filters/tree/right-click Dump), Live TCP View
- **Static analysis**: System Info, Process View, Recent Files, Prefetch, Amcache, Autorun, Event Log, Netstat, Browsing History
- **Offline Hive**: Offline Registry parsing (with optional VSS snapshot); drill-down correlation
- **Detachable tabs**: Dynamic tabs can be detached to a separate window

---

## Beta Disclaimer

- This is a **Beta release** for evaluation and testing. Do not rely on it for formal forensics or legal evidence without adequate validation.
- Please read **USAGE.md** and this README before use.
- The executable may be signed with a **nine-security Inc.** certificate; trust and validation are your responsibility.

---

## Version and License

- **Version**: See the release tag or zip filename (e.g. 1.0.x-beta)
- **Copyright**: Copyright (c) 2026 nine-security Inc.
- This distribution provides documentation here and binaries via Releases; see your license terms from the publisher.
