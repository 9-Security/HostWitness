# HostWitness — Beta Release

**HostWitness** is a Windows single-host live forensics and activity correlation tool. It collects and correlates processes, network connections, timeline events, and static artifacts on one machine.

This repository is a **Beta release** distribution. It contains only the compiled executable, usage documentation, and file integrity hashes—**no source code**.

---

## System Requirements

- **OS**: Windows 10/11 (x64)
- **Privileges**: Most features work with normal user rights; **some require Administrator** (e.g. VSS snapshots, Offline Hive, Create Dump for other processes)
- **.NET**: The executable is **self-contained**; you do **not** need to install .NET

---

## Download and Verification

| File | Description |
|------|-------------|
| **HostWitness.exe** | Single executable (approx. 150+ MB, self-contained) |
| **HostWitness-beta-*.zip** | Compressed archive of the exe for easier download and backup |
| **SHA256SUMS.txt** | SHA-256 hashes of the above files for integrity verification |

### How to verify integrity (Windows PowerShell)

After downloading, run in the same folder:

```powershell
# Verify exe
Get-FileHash -Algorithm SHA256 .\HostWitness.exe | Format-List

# Verify zip
Get-FileHash -Algorithm SHA256 .\HostWitness-beta-*.zip | Format-List
```

Compare the output `Hash` with the corresponding value in **SHA256SUMS.txt**. If they match, the file has not been modified.

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

- **Dynamic analysis**: Timeline, Live Stream, Live Process (Procmon-like filters/tree/right-click Dump), Live TCP View (TCPView-like)
- **Static analysis**: System Info, Process View, Recent Files, Prefetch, Amcache, Autorun, Event Log, Netstat, Browsing History
- **Offline Hive**: Offline Registry parsing (with optional VSS snapshot); Drill-down correlation
- **Detachable tabs**: Dynamic tabs can be detached to a separate window; toolbar icons match the main window
- **Startup default**: Opens on the **System Info** tab

---

## Beta Disclaimer

- This is a **Beta release** for evaluation and testing. Do not rely on it for formal forensics or legal evidence without adequate validation.
- Please read **USAGE.md** and this README before use. See documentation for known limitations and risks.
- The executable is signed with a **nine-security Inc.** self-signed certificate; the description URL may point to this repository or the official page.

---

## Version and License

- **Version**: See filename or SHA256SUMS.txt (e.g. 1.0.0-beta)
- **Copyright**: Copyright (c) 2026 nine-security Inc.
- This distribution provides only the executable and documentation; see project or official sources for license terms.
