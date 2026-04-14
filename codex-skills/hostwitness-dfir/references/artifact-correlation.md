# HostWitness Artifact Correlation

Use this file when reconstructing activity or deciding how much weight to give a HostWitness finding.

## General rule

Prefer claims that are supported by two or more artifact families. A single artifact may be interesting, but should rarely carry the whole conclusion alone.

## Artifact quick guide

### Timeline View

Best for:

- ordering cross-source activity
- seeing process, file, registry, browser, and network activity together
- narrowing a time window before deeper review

Not enough by itself for:

- proving execution without supporting artifacts
- proving completeness when export caps or drop counters are present

Corroborate with:

- Prefetch
- Amcache
- Event Log
- MFT
- Recent Files, LNK, or JumpList

### Event Log

Best for:

- system and security context
- service, login, policy, and subsystem events
- anchoring activity to native Windows telemetry

Limitations:

- logs may be incomplete, cleared, filtered, or unavailable without sufficient rights
- log presence does not mean all related actions were captured

Corroborate with:

- Timeline ordering
- Prefetch or Amcache for execution context
- Registry or Autorun for persistence context

### Prefetch

Best for:

- evidence consistent with program execution
- run count and last run time clues
- referenced-file context

Limitations:

- missing prefetch can be caused by privilege limits, disabled Prefetch, or lack of `.pf` files
- Prefetch alone does not prove user intent or full process lineage

Corroborate with:

- Amcache
- Event Log
- MFT path/time changes
- Recent Files or browser/download evidence

### Amcache

Best for:

- application presence and historical execution/install clues
- publisher, version, path, SHA1, and file metadata context

Limitations:

- may not reflect current presence
- does not alone prove a specific user launched the program at a specific time

Corroborate with:

- Prefetch
- Event Log
- MFT
- Recent Files, LNK, or JumpList

### Recent Files, LNK, and JumpList

Best for:

- user interaction clues
- recently opened files and paths
- linking user activity to documents or executables

Limitations:

- indicates access-related behavior, not necessarily malicious execution
- may reflect shell interactions more than process creation semantics

Corroborate with:

- Prefetch
- MFT
- Browser history for download origin
- Timeline process context

### Registry and Autorun

Best for:

- persistence review
- MRU and Run-key style activity
- user or system configuration clues

Limitations:

- live registry queries are explicitly non-forensic and should be treated as supportive only
- key timestamps and values may be incomplete or manipulated

Corroborate with:

- Offline Hive findings
- Event Log
- Prefetch or process evidence
- MFT or file presence

### MFT

Best for:

- path existence, file status, and timestamp comparisons
- deleted versus in-use state
- time-stomp suspicion via `$STANDARD_INFORMATION` versus `$FILE_NAME`

Limitations:

- disk-based loads can hit the 100 MB read cap and become partial
- volume tabs are independent; there is no merged cross-volume view
- odd record layouts may still need external validation

Corroborate with:

- Prefetch
- Amcache
- Recent Files and JumpList
- Event Log

### Browser History

Best for:

- URL and visit evidence
- download origin clues
- user browsing sequence around a suspected event

Limitations:

- browser history alone does not prove file execution
- may be incomplete by browser support, profile availability, or timing

Corroborate with:

- Prefetch
- Amcache
- Recent Files or downloads in MFT
- Timeline process or network activity

### Live Process and Live TCP

Best for:

- current state triage
- active processes, process tree context, and current connections
- near-real-time follow-up during live response

Limitations:

- live state is transient and may differ seconds later
- ETW throttling, UI backpressure, or privilege limitations can reduce completeness
- do not overstate current-state observations as historical proof

Corroborate with:

- Timeline
- Event Log
- Snapshot export
- process dump only when explicitly justified

## Common investigation patterns

### Suspected execution of a tool or payload

Strong pattern:

- Prefetch entry consistent with execution
- Amcache entry for the same path or hash
- supporting Timeline or Event Log activity
- MFT evidence showing presence or write activity around the same period

### Suspected persistence

Strong pattern:

- Autorun or Run-key entry
- matching file path in MFT or offline hive
- execution clues from Prefetch or Amcache
- supporting Event Log or Timeline activity after reboot or login

### Suspected user access to a document or dropped file

Strong pattern:

- RecentDocs, LNK, or JumpList entry
- matching file path in MFT
- Prefetch for the associated viewer or executable
- browser/download context if the file originated online

### Suspected time-stomping or anti-forensics

Strong pattern:

- `Time-stomp?` flagged in MFT
- timestamp inconsistency against Prefetch, Event Log, or Timeline
- suspicious mismatch between claimed file age and nearby execution or write artifacts

## Reporting guidance

When the evidence is mixed, write the conclusion at the strongest defensible level, for example:

- `Observed artifacts are consistent with execution of X, but no single source independently proves user intent.`
- `Persistence via the Run key is supported by registry and file-system artifacts; execution after persistence is suggested by Prefetch.`
- `The file appears in MFT and RecentDocs, which is consistent with user access, but execution is not established.`


