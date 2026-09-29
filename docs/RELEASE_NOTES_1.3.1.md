# HostWitness 1.3.1

This release is a **security and supply-chain hardening update** addressing an upstream SQLite vulnerability and updating transitive dependencies to maintain zero-vulnerability compliance across all modules.

> Distribution is the signed, self-contained single-file `HostWitness.exe` (win-x64, .NET 8 bundled — no runtime install required). Self-signed (`CN=nine-security Inc.`); Windows will show an "unknown publisher" prompt.

Builds on everything in 1.3.0 (multi-host evidence collection and central case repository publish).

---

## Security & Supply Chain Fixes

- **Patched SQLite Memory Corruption Vulnerability ([GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) / CVE-2025-6965)**:
  - Upgraded native SQLite engine via `SQLitePCLRaw.bundle_e_sqlite3` to version `3.0.5` across `WinDFIR.Core` and `WinDFIR.Providers`.
  - Fully eliminates the High-severity vulnerability in offline timeline SQLite export/import and browser database parsing.
- **Dependency Audit & SBOM Verification**:
  - Full transitive dependency verification completed via `dotnet list package --vulnerable --include-transitive`.
  - All 5 projects (`Core`, `Providers`, `UI`, `Agent`, `Tests`) report 0 vulnerable packages.

---

## Verifying the download

See [`VERIFY_AND_SMARTSCREEN.md`](VERIFY_AND_SMARTSCREEN.md) for the full SmartScreen and integrity-verification guide.

```powershell
Get-FileHash .\HostWitness.exe -Algorithm SHA256
```

Expected SHA256 for this release's `Release\HostWitness.exe` (win-x64, self-contained single file):

```
080ABF5828D27994072F9BB2FE608F2E8E09EBC0040D80DF41EF4C0F47A2D335
```
