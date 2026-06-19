# After downloading: SmartScreen warning & file verification

> This page is for **people running a published release**. If you build from source, see *Building* in the root `README.md`.

## Why "Windows protected your PC" (SmartScreen) appears

HostWitness is an **open-source single-host forensics tool** and is **not signed with an EV code-signing certificate**. Windows SmartScreen shows a warning for programs whose publisher reputation is not yet established (low download count). **This is expected — it does not mean the file is corrupt or malicious.** Because a forensics tool reads the raw disk (`\\.\PhysicalDrive0`), dumps process memory, and uses privileged operations, it is *more* likely than ordinary software to be flagged by SmartScreen or antivirus.

To run it anyway:

1. **Verify the file with SHA256 first** (below) to confirm you have the official build — this matters more than dismissing the prompt.
2. Once verified, on the SmartScreen screen click **"More info" → "Run anyway"**;
   or right-click the file → **Properties** → tick **Unblock** → OK.

> If you want to remove this warning under your own name, the cheapest public route is a **Certum Open Source code-signing certificate (~US$50/yr, cloud signing, no hardware token)**; only an **EV certificate (~US$249/yr)** clears SmartScreen immediately. Until enough download reputation accrues, OV-level certificates still show the warning.

## Verify file integrity with SHA256

After downloading, before running, compute the hash in PowerShell:

```powershell
Get-FileHash .\HostWitness.exe -Algorithm SHA256
```

Compare the `Hash` value against the SHA256 published for that release (recorded per version in `docs\RELEASE_NOTES_<version>.md` — e.g. see *Verifying the download* in `docs\RELEASE_NOTES_1.3.0.md`; the GitHub Release asset notes carry the same hash).

One-line automatic comparison (replace `<EXPECTED_SHA256>` with the published value):

```powershell
if ((Get-FileHash .\HostWitness.exe -Algorithm SHA256).Hash -eq '<EXPECTED_SHA256>') { 'OK: hash matches' } else { 'WARNING: hash mismatch, do not run' }
```

A **mismatch** means the file was corrupted in transit or is not the official build — **do not run it**; re-download from the official source.

## Highest trust: build from source

If you do not trust any prebuilt binary, the most reliable option is to build it yourself: clone this repository and run `cmd.exe /d /c .\publish.cmd` to produce `Release\HostWitness.exe` (self-contained single file; see *Building* in the root `README.md`). The project is open source precisely for this reason — the code is auditable and the build is reproducible.
