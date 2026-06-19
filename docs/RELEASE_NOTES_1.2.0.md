# HostWitness 1.2.0

This release adds **cross-source anomaly detection** — a practical answer to a live tool's fundamental blind spot. A user-mode tool cannot guarantee it sees what a kernel/firmware implant hides, but a **discrepancy between what a live API reports and what the raw/offline source shows** is a high-value tampering/hiding indicator. HostWitness now compares the two views across four artifact types and flags the differences as advisory tripwires.

> Distribution is the signed, self-contained single-file `HostWitness.exe` (win-x64, .NET 8 bundled — no runtime install required). Self-signed (`CN=nine-security Inc.`); Windows will show an "unknown publisher" prompt.

Builds on everything in 1.1.0 (collection-trust dashboard, attack-chain artifacts, offline `.evtx`/SRUM/BITS/WMI parsing, JumpList DestList fix, cross-volume merged MFT, live truncation warning).

---

## New: cross-source anomaly detection (live API vs raw/offline)

Findings appear as `Category="Anomaly"` events at **Low/advisory confidence** — "investigate", never "confirmed rootkit", because benign causes exist (disabled/removed entries, collection timing). Four comparisons ship:

- **Services** — live WMI `Win32_Service` vs the raw SYSTEM-hive Services. (Also adds a live services view, which the tool previously lacked entirely.)
- **Scheduled tasks** — on-disk Task XML (`…\System32\Tasks`) vs the registry `TaskCache`. Catches a task whose XML was deleted to hide it from the Task Scheduler UI while it still runs from the registry.
- **Run keys** — the live registry API vs the raw hive (HKLM/HKCU `Run`). *Requires live registry collection to be enabled (off by default under the forensic policy).*
- **Processes** — the native process list (`NtQuerySystemInformation` path) vs WMI `Win32_Process`, captured live. Each discrepancy is **re-confirmed** by re-querying to filter out processes that start/exit between the two snapshots.

An item in the raw/second source but missing from the live view (`MissingFromLive`) is a possible hiding indicator; the reverse (`MissingFromOffline`) is a possible memory-only/injected item.

**How to run:** the services/tasks/run-key checks run automatically in the agent (after collection, before export) and on demand in the UI (`Advanced ▸ Cross-Source Anomalies...`). The process check runs as a collection-time provider (it needs the two live APIs, so it cannot be re-run from a loaded snapshot).

## Also in this release

- A standalone **live services view** (WMI `Win32_Service`), in the default live collection set.
- `TaskCache` task paths are now decoded in full (previously truncated to ~33 characters) and surfaced as readable paths rather than hex.

---

## Honest bounds

These checks **raise the adversary's cost and catch common hiding — they are not a guarantee.** The root limits of any user-mode live tool remain: a sufficiently privileged kernel (ring 0) or firmware/hypervisor (ring -1/-2) implant can lie to *both* the live API and, for the process check, the second API too; and running the tool itself perturbs system state. The process comparison in particular uses two user-mode APIs — true rootkit resistance would require raw memory / handle-table access, which this build does not do. Treat anomalies as leads to corroborate and as a signal to escalate to offline/memory forensics, not as proof. See `docs/LIMITATIONS.md` §26 and `docs/ASSESSMENT.md` (P6).

## Verifying the download

See [`VERIFY_AND_SMARTSCREEN.md`](VERIFY_AND_SMARTSCREEN.md) for the full SmartScreen and integrity-verification guide.

```
Get-FileHash .\HostWitness.exe -Algorithm SHA256
```

(Compare against the SHA256 published with this release.)
