# CNC verification code audit — 2026-08-27

## Decision

**QUARANTINED — NOT PRODUCTION APPROVED.** Do not enable CNC setup verification,
install a production key, or use bench packages v1, v2, or v3 as a production
interlock. No further physical CNC work is requested by this audit.

The real VF-3SS on NGC `100.21.000.1001` established useful partial evidence:
Setting 23 protected O9001/O9002 from ordinary access; the generic G65 hook and
public response arithmetic worked; correct and incorrect six-digit entry,
failure-before-return, persistent-variable cleanup, Reset cleanup in macro v4,
E-stop recovery, and controller reboot cleanup were observed. Those results do
not overcome the blocking findings below.

## Findings

### C1 — M109 timeout enforcement is not valid (critical, open)

Macro v5 checked `#3001` after the sixth M109 input in source order, but the VF-3SS
accepted the otherwise correct response after the operator waited at least 130
seconds at the first prompt. No alarm was raised. This violates the configured
120-second CNC interlock even though the Server independently rejects a late SVS.

The generator's static test asserted textual ordering only. Haas documents that
macro expressions execute during look-ahead, that `G103 P1` still interprets one
block ahead, and that M109 specifically requires a following loop that waits for
a nonzero response. The candidate has direct IF checks rather than the documented
loop and has no commissioned execution barrier proving that the post-input timer
read occurs after operator input.

Disposition: macro versions 3–5 and bench packages v1–v3 are quarantined. Do not
create a speculative v6. Before another physical test, obtain an internally
reviewed design for a fresh post-M109 timer read—such as a separately protected finalizer
call or another documented execution barrier—or replace M109 with a commissioned
input mechanism. Add an automated structural test for the approved pattern, while
retaining physical proof as the acceptance gate.

### C2 — Event sequence is not monotonic across reboot/wrap (critical, open)

O9001/O9002 derive `SEQ` from `#3001`. Haas defines `#3001` as milliseconds since
power-on, so controller reboot resets the sequence and eventual wrap can repeat it.
The Server's per-source sequence logic expects monotonic evidence. A reboot can
therefore turn later legitimate OLC/SVS/SVF events into gap/out-of-order evidence,
and date/time-based IDs do not repair the sequence invariant.

Disposition: define a commissioned sequence epoch/counter contract that survives
or explicitly resets at connection epochs without becoming workflow authority.
Do not approve the current `#3001` sequence as production evidence.

### C3 - Verification results lacked challenge correlation (critical, Server fixed; CNC open)

The quarantined v3-v5 `SVS`/`SVF` records carry the NC identity and macro version
but do not repeat the Offset Loader release token or nonce. Because the Server had
selected the latest pending session by Machine, a delayed result from an older
challenge could have resolved a newer challenge for the same Machine and NC
release. NC identity alone cannot distinguish those sessions.

The Server protocol and development simulator now fail closed: `SVS`/`SVF` must
repeat the exact six-digit NC identity, current Offset Loader release token, and
nonce. A token mismatch records `stale_offset_loader`; a nonce mismatch records
`offset_loader_not_executed`; neither resolves the pending session. Automated
regression coverage delivers an old result after a newer OLC and proves that the
new session stays `PENDING` until its own correlated result arrives.

Disposition: do not rewrite or relabel the forensic v3-v5 macro source. It is
intentionally incompatible with the hardened Server contract. After the internal
timer/sequence engineering gates are satisfied, a newly numbered candidate must carry
the correlation fields in both SVS and SVF and pass the delayed-result regression
before any bounded physical retest.

### H1 — MDC cross-talk could starve DPRNT ingestion (high, fixed in source)

Haas documents that a reply requested by one MDC connection is sent to every MDC
connection. The old Server assumed the next line was always its own Q500 response.
The commissioning capture tool simultaneously requested Q101/Q102/Q500, causing
the installed Server to parse another response as Q500, throw for missing PARTS,
restart its poll adapter, and skip the DPRNT drain for that cycle.

Source correction:

- the passive capture tool skips MDC by default;
- the Server filters replies by expected response type;
- documented `STATUS, BUSY` is represented with unavailable program/counter data
  instead of failing the whole poll;
- degraded MDC data no longer prevents the same snapshot from draining DPRNT.

Automated Haas tests pass. This fix is not present in the currently installed
Windows Service until a new Server MSI is built and deliberately installed.

### H2 — Dead DPRNT sockets could survive controller reboot (high, fixed in source)

`TcpClient.Connected` reports the last known state and is not a liveness probe.
The old reader returned immediately when `DataAvailable` was false, so a socket
closed by controller reboot could remain cached forever and never reconnect.

Source correction: the reader now detects a readable socket with zero available
bytes as disconnected, disposes it, and reconnects. A loopback peer-close test
covers the behavior. Deployment remains pending.

### H3 — Competing capture clients were unsafe and ambiguous (high, fixed in source)

The live helper previously connected even when the installed Server already owned
the Haas DPRNT endpoint. Both connections could appear established while only one
received useful output. The helper now refuses a competing local established
connection by default. An explicit override is for isolated diagnostics only.

### M1 — Commissioning evidence exposed raw nonce values (medium, fixed in source)

The live helper printed and logged the raw OLC nonce. The revised helper keeps it
only in memory for the public bench calculation, redacts it in console/file output,
validates the expected macro version, and rejects a second challenge in one session.

### M2 — Static macro/package tests overstated assurance (medium, open)

The tests prove generated text, hashes, public arithmetic, no-motion sample files,
and event formatting. They cannot prove Haas look-ahead timing, M109 behavior,
protected execution, transport ownership, alarm ordering relative to real cutting,
or power/reset behavior. Test and report wording must keep those boundaries explicit.

### M3 — Installed service recovery is absent (medium, installation pending)

Windows recorded one unexpected service termination during the commissioning
window, interleaved with intentional stop/start attempts. The audit cannot assign
that event to a code crash without a clean reproduction. The administrative check
on 2026-08-27 found the installed 0.1.40 service `Running` and `Automatic`, but with
no configured failure actions. Installer 0.1.43 now compiles the WiX utility
service configuration for two bounded 60-second restarts, no third automatic
action, and a one-day failure-count reset. Its final build and extraction passed
with zero warnings, but the non-elevated install attempt correctly failed with
Windows Installer 1730. The installed service remains 0.1.40 until an administrator
applies the verified MSI and confirms the policy with `sc.exe qfailure`.

### M4 — Quarantined source could still be regenerated too easily (medium, fixed)

The generator and ZIP builders previously relied on warning text and a manifest
status after producing the physically failed v5 source. That was insufficient as
an accidental-use barrier. They now fail before creating output unless the caller
supplies the deliberately long `-AcknowledgeQuarantinedAuditOnly` switch. The
switch exists only to reproduce test fixtures and does not change the manifest,
warning text, commissioning decision, or disabled Server setting. Automated tests
exercise the default refusal and the acknowledged artifact's quarantine status.

### M5 — Macro-v6 retained the sixth M109 ASCII value (critical, v6 quarantined)

The bounded VF-3SS R2 attempt on 2026-08-30 reproduced alarm 903 while
`#10500` still contained ASCII value `55`; `#10501-#10503` were empty. Source
review found that O9002 accumulated digit six into its local result but did not
clear the configured M109 response variable before calling O9003. The operator
also reported an M30 return after several attempts, but Server evidence retained
multiple OLC/SVF attempts and no SVS, so that observation is ambiguous rather
than accepted interlock evidence. Retained temporary authority independently
fails the hard stop. Macro v6 and both physical ZIPs are quarantined.

A distinct macro-v7 desk candidate clears the response variable after every
accepted digit and clears all four temporary handshake variables immediately on
finalizer entry, before any result branch. It bumps the reported macro version
to 7 and has dedicated structural regression coverage. This is code/test
evidence only; v7 is not approved for controller loading or Server enablement.

## Confirmed safe Server behavior

- Server-created verification sessions expire by Server UTC time.
- A late SVS cannot resolve an expired session successfully.
- A delayed SVS from an older release token or nonce cannot resolve a newer
  pending session.
- Tablet status omits the response after expiry or invalidation.
- A newer Offset Loader supersedes a live session.
- Macro-version, NC identity, Machine, Run, and current-release mismatches reject
  verification evidence.
- TV/tablet authorization boundaries remain independent of the CNC macro defect.

## Exit criteria before another CNC visit

1. Have the site's qualified CNC controls engineer and Meimad production owner
   approve a documented M109/finalizer or replacement-input design using the
   [internal engineering worksheet](haas-internal-engineering-review-2026-08-27.md).
2. Replace the mixed `#3001`/parts-counter sequence semantics with one reviewed
   reboot/wrap contract shared by every emitted event type.
3. Add structural/unit tests for the approved macro pattern and connection epoch.
4. Run the full Server and Windows regressions and build a new Server MSI.
5. Rehearse sole-owner MDC/DPRNT capture on a simulator or disconnected bench.
6. Prepare one short, prewritten physical script covering only the unresolved
   cases; do not iterate macro versions at the machine.
7. Keep `cnc_verification_settings.enabled=false` until the checklist and both
   sign-offs are complete.

## Authoritative references

- Haas M109 Interactive User Input
- Haas G103 Limit Block Look-Ahead
- Haas Mill Macros (`#3001` and macro look-ahead)
- Haas NGC Machine Data Collection (including broadcast replies and Q500)
- `docs/cnc-commissioning-checklist.md`
- `docs/haas-verification-response-algorithm.md`
