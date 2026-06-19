# HostWitness 1.3.0

This release adds **multi-host evidence collection** — the step from "a snapshot per box" to "one case, many hosts in one place." Every investigated host's agent (or the desktop UI) can now **publish its finished, hash-verified bundle into a central case repository**, so an analyst gathers collections from across an engagement in a single location instead of copying folders back by hand.

> Distribution is the signed, self-contained single-file `HostWitness.exe` (win-x64, .NET 8 bundled — no runtime install required). Self-signed (`CN=nine-security Inc.`); Windows will show an "unknown publisher" prompt.

Builds on everything in 1.2.0 (cross-source anomaly detection, live services view, full `TaskCache` decode) and 1.1.0.

---

## New: publish to a central case repository (P5 multi-host)

A finished snapshot bundle can be published to a shared destination over either of two transports, both honoring the same `IArtifactSink` contract:

- **Filesystem sink** — a shared folder, UNC share, or mounted bucket. Zero infrastructure: every host's agent drops its bundle into one case folder.
- **HTTP intake** — for hosts that cannot reach a shared path but can reach an operator-run intake server (`HttpListenerBundleIntakeServer` over `BundleIntakeService`).

Repository layout is `<repo>/<hostname>/<collectionId>/`. The `collectionId` is a stable per-collection GUID written into `manifest.json`, so it survives copy/rename and serves as the idempotency key.

Every publish is:

- **Integrity-gated** — the source bundle is re-verified against its own `hashes.txt` (including the reverse "no undeclared files" check) before anything is sent, and the assembled copy is verified again before it is exposed under its final name.
- **Idempotent** — re-publishing a collection that is already present and verifiable is a no-op, never a duplicate or a corrupt overwrite.
- **Resumable** — an interrupted transfer re-sends only the files that are missing or differ, then atomically renames into place.

**How to run:**

- Agent: `HostWitness.Agent.exe C:\Temp\out 60 --repo=\\fileserver\cases\IR-2026 [--repo-token=<secret>]` (a `http(s)://` value routes to the HTTP intake; anything else is a filesystem path).
- UI: **File ▸ Publish to Case Repository…**

The HTTP intake authenticates with a shared bearer token (constant-time compared); pair it with TLS termination before exposing it beyond a trusted LAN. Full deployment steps (URL ACL, `sslcert`, auth) are in `docs/CaseRepositoryIntake說明.md`.

## Hardening in this release

Following a focused review of the publish/intake path:

- HTTP publishes now surface request timeouts and transport/5xx errors as a `Failed` result instead of throwing, matching the documented sink contract — large bundles over slow links no longer fail with an opaque exception.
- The filesystem sink prunes a stale `.partial` staging directory so an interrupted publish can always resume instead of wedging on the integrity reverse-check.
- `ResolveBundleDirectory` selects the newest `snapshot_*` when a parent holds several, so a stale earlier export is never published by mistake.
- Path segments derived from a manifest now neutralize Windows reserved device names (`CON`, `NUL`, `COM1`…).
- The in-process publish lock is reference-counted (no per-collection leak in a long-lived intake server), and file receipt is serialized against finalize so a late upload cannot be silently dropped.

---

## Honest bounds

The case repository improves **gathering and integrity**, not collection coverage — the root limits of any user-mode live tool (documented in 1.2.0 and `docs/LIMITATIONS.md`) are unchanged. The HTTP intake is a minimal, operator-run service: it has no built-in upload size/rate limit and, without TLS, the bearer token travels in plaintext — run it on a trusted LAN or behind a reverse proxy that terminates TLS and enforces limits. Two *separate processes* publishing the *same* `collectionId` to a shared filesystem are not cross-process locked (the per-collection GUID makes this effectively impossible in practice).

## Verifying the download

See [`VERIFY_AND_SMARTSCREEN.md`](VERIFY_AND_SMARTSCREEN.md) for the full SmartScreen and integrity-verification guide.

```
Get-FileHash .\HostWitness.exe -Algorithm SHA256
```

Expected SHA256 for this release's `Release\HostWitness.exe` (win-x64, self-contained single file):

```
A84B07AB22BE09ABEF08ABD54CE28C2A412701DF2AEC711593E774B9079BE595
```
