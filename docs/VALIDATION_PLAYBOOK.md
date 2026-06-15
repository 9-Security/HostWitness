# Validation Playbook

The biggest gap to "real-world trustworthy" is **validation breadth**: parsers are currently proven against a single host / OS build. This playbook is how to widen that — the part that needs human-collected samples. Pairs with the automated tests in `WinDFIR.Tests/Validation/`.

## A. Differential corpus (parser correctness across diverse systems)

**Goal:** prove each parser matches an authoritative reference tool, on many Windows versions and locales.

**Per sample set, collect (KAPE or manual):**
- Artifacts: `SRUDB.dat`, `qmgr.db`, `OBJECTS.DATA`, `\System32\Tasks\` + SOFTWARE/SYSTEM hives, `AutomaticDestinations\*`, `.evtx`, `$MFT`, Amcache, Prefetch.
- Reference ground truth for the same artifacts: SrumECmd, JLECmd, PECmd, AmcacheParser, MFTECmd, `Get-WinEvent` (run the EZ tools yourself).

**Coverage target (diversity is the point):**
- Windows 10 21H2 / 22H2; Windows 11 22H2 / 23H2 / 24H2; one Server SKU.
- At least one **non-English locale** (zh-TW already surfaced WMI/service-type quirks) and one EN-US.
- A busy/real machine (many tasks/services) and a fresh install.

**Wire it in:** the existing differential tests (`SrumDifferentialTests`, `JumpListDifferentialTests`) are corpus-pattern — they take an artifact + its reference CSV and assert agreement at scale (count + per-row multiset). For each new sample set, add a case pointing at its paths (or refactor to enumerate a `samples/<id>/` folder convention). A divergence = a parser bug to fix before trusting that artifact on that OS.

## B. Positive-detection samples (prove detectors fire on real malice)

**Goal:** the synthetic positive tests (`PositiveDetectionTests`) prove the *logic* fires; this proves it on *real* artifacts. Use a disposable test VM, never production.

Generate known-bad, then collect the artifacts and confirm HostWitness flags them:

| Technique | How to create (test VM, admin) | Should appear as |
|---|---|---|
| WMI subscription persistence | `__EventFilter` + `CommandLineEventConsumer`/`ActiveScriptEventConsumer` + `__FilterToConsumerBinding` (PowerShell `Set-WmiInstance`) | WMI Filter/Consumer/Binding events |
| Malicious scheduled task | `schtasks /create` running `powershell -enc …` | ScheduledTask event with the encoded command |
| Run-key persistence | add HKCU/HKLM `…\Run` value → exe | Run-key event (offline hive) |
| BITS download | `Start-BitsTransfer` from a known URL | BITS event with the URL + dest path |
| Hidden process | run a tool that hides a PID from one API | **Cross-source Process anomaly** |
| Hidden service / orphaned TaskCache | create a service then delete its SCM entry but leave the hive key; delete a task's XML but leave TaskCache | **Cross-source Service / Task anomaly** |

For each: record the ground truth (what you planted), run HostWitness (`--srum/--bits/--wmi/--evtx`, or live), and confirm the artifact/anomaly is surfaced. Capture the sample so it can become a regression test.

## C. Robustness / fuzz (never fabricate evidence)

For every parser, feed: truncated files, zeroed files, wrong-size records, corrupt headers, dirty ESE DBs, non-UTF16 where UTF16 is expected. Assert: **no crash, no fabricated rows, graceful "couldn't parse"**. This codifies the project's core promise. Start from the existing parser tests and add malformed-input cases.

## What the automated suite already does
- `Validation/SrumDifferentialTests` — Network Data Usage vs SrumECmd at scale (count + ≥98% row multiset).
- `Validation/JumpListDifferentialTests` — DestList vs JLECmd across all files (≥95% entry coverage, ≥80% path agreement).
- `Validation/PositiveDetectionTests` — planted-bad fires for PowerShell/ScheduledTask/WMI/BITS/cross-source.
- `CrossSourceRealHiveTests`, `SrumParserGroundTruthTests`, `BitsParserTests` (real-sample, gated on presence).

All sample-dependent tests are **gated on file presence**, so the suite stays green on machines without the corpus. Drop a corpus in and they activate.
