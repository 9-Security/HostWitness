1. Live System Assumptions

The tool operates under the following mandatory assumptions:

The system is running and actively changing

Volatile data may be lost at any time

System state may be partially compromised

Anti-forensic techniques may be present

2. Trust Assumptions

The following components are not implicitly trusted:

System timestamps

Process lists

Event logs

Registry contents

Security products installed on the host

All collected data must be treated as potentially incomplete or manipulated.

3. Privilege Assumptions

Administrator or SYSTEM privileges are not guaranteed.

Some artifacts may be inaccessible due to privilege limitations (e.g. locked registry hives, event logs). The tool uses VSS when running as Administrator with the Volume Shadow Copy service running; otherwise it falls back to live paths and reports warnings.

The tool must not attempt privilege escalation.

Privilege level must be: detected, recorded, reflected in output limitations.

4. Environmental Assumptions

Endpoint may be production-critical

Endpoint may be unstable or under attacker control

Network connectivity may be limited or monitored

The tool must not:

Depend on external network resources

Modify system configuration

5. Interpretation Assumptions

Absence of evidence does not imply evidence of absence

Live response data represents a point-in-time snapshot

All analytical conclusions are probabilistic, not deterministic

---

*Document last updated: 2026-02-02 (current state sync).*