# Haas NGC protected setup-verification technical spike

## Status and hard gate

Milestone C is prepared but **not physically proven**. Haas publicly documents protected `O9000` storage, G65 macro calls, general/system macro variables, programmable alarms, and TCP DPRNT. Its published NGC macro-variable table does not identify a supported variable that unambiguously returns the active top-level or caller NC program from inside a protected macro. Meimad has therefore selected the explicit generic-hook fallback: every newly approved NC release carries a unique release identity into the protected call. This repository decision does not prove the protected macro or interlock, and `cnc_verification_settings.enabled` must remain false until the real controller test below is completed.

This spike performs no cutting motion, offset write, CNC variable write from the Server, planning mutation, or automatic macro deployment. A Haas Factory Outlet/controller specialist must approve the candidate read-only identity source and temporary variable range before the probe program is loaded.

## Supported facts

- Setting 23 prevents normal viewing or alteration of files in the `09000` Memory folder. This is the candidate protection mechanism, subject to an operator-access test on the actual control.
- G65 calls a macro subprogram and creates nested local variables. Passing an identity argument would prove only a caller-supplied value, not an intrinsic active-program identity.
- NGC DPRNT can emit over the TCP port selected by Settings 261 and 263. Haas documents DPRNT literal text as letters plus `+`, `-`, `/`, `*`, and spaces; `*` becomes a space. Meimad v1 therefore uses `/` separators and short alphabetic event codes rather than pipes, equals signs, or underscored names.
- MDC `Q500` reports the control's active program locator externally. That is valuable comparison evidence, but it does not prove what a protected macro can independently read.
- MDC `Q600` reads a specified macro/system variable. The commissioning capture tool allows only Q101, Q102, Q500, and explicitly requested Q600 reads and has no write command path.

Official references: [Haas 9xxx edit lock](https://www.haascnc.com/service/codes-settings.type%3Dsetting.machine%3Dmill.value%3DS23.html), [Haas mill macros](https://www.haascnc.com/service/online-operator-s-manuals/mill-operator-s-manual/mill---macros.html), [Haas G65](https://www.haascnc.com/service/codes-settings.type%3Dgcode.machine%3Dmill.value%3DG65.html), [Haas DPRNT](https://www.haascnc.com/service/troubleshooting-and-how-to/how-to/communication-with-external-devices---dprnt.html), and [Haas Machine Data Collection](https://www.haascnc.com/service/online-manuals/next-gen-control-electrical---service-manual/ngc---machine-data-collection.html).

## Required HFO/controller decision before loading a probe

Record all of the following in the evidence file. Do not guess:

1. Exact Machine model, NGC software version, and Macro option availability.
2. Supported read-only variable or controller mechanism claimed to return the top-level/caller O-number from inside G65/O9000 execution.
3. Whether that value identifies the caller rather than the protected macro itself, the previous block's `O` address, a selected-but-not-running program, or an externally supplied argument.
4. Four unused general-purpose temporary variables, checked against probing, pallet, robot, postprocessor, and existing customer macro programs.
5. Approved protected program numbers and whether Setting 23 actually prevents ordinary operator view/edit/delete/copy on this software version.
6. DPRNT formatting behavior for slash separators, alphabetic codes, numeric formatting, leading zeros/spaces, reconnects, Reset, and power cycle.

## Read-only evidence capture

Run from an isolated commissioning workstation while the Machine is stopped and cleared for a no-motion test:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\haas-verification-spike.ps1 `
  -MachineLabel VF3SS-COMMISSIONING `
  -HostName <machine-ip> `
  -MdcPort 5051 `
  -DprntPort 8080 `
  -CaptureSeconds 120 `
  -CandidateReadOnlyVariables <HFO-approved-variable> `
  -OutputPath .\.diagnostics\haas-verification\vf3ss-identity-001.json
```

The network address is intentionally not written to the evidence JSON. The output location is ignored operational evidence and must be reviewed before any sanitized conclusion is copied into version-controlled documentation. Never pass the secret, nonce, response, state, or release-token variables as candidates.

During the capture, run only an HFO-reviewed, no-motion protected probe. Identity probes must emit a non-production line beginning `MEIMADSPIKE/`, containing the candidate variable number/value, the protected macro's own O-number, and a test-run identifier. Algorithm-vector probes use the stricter `MEIMADSPIKE/V/1/TEST/...` contract in the response-algorithm document and are graded into the evidence JSON automatically. Neither form may emit `MEIMAD/V/1`, because that prefix is reserved for validated operational ingestion. Compare the protected-macro identity value with Q500 before and after each case:

| Case | Top-level program | Call form | Required observation |
|---|---:|---|---|
| A | O1234 | `G65 P<probe>` | Candidate equals O1234 while inside probe. |
| B | O4321 | `G65 P<probe>` | Candidate changes to O4321 without editing probe. |
| C | MDI | `G65 P<probe>` if supported | Behavior is explicit and fail-closed. |
| D | O1234 | nested macro calls probe | Top-level versus immediate-caller meaning is established. |
| E | O1234 | Reset during probe | No stale success/identity survives. |
| F | O1234 | power cycle, rerun | Variable persistence and sequence reset behavior are recorded. |

Repeat A and B at least five times. Also run once with Setting 23 off to inspect/load, then on to prove ordinary access restriction. Do not test alarms with spindle or feed motion enabled.

## Decision outcomes

- **PASS — intrinsic identity:** all repetitions return the correct stable top-level NC identity without a caller-supplied identity. Milestone C may then define the response algorithm using that approved source.
- **FAIL — macro identity only:** the candidate returns the protected O9000 program or immediate nested macro. This cannot verify the NC release.
- **FAIL — supplied/previous-block value:** the result depends on a G65 argument or previous `O` address. This is replayable and cannot be presented as intrinsic NC verification.
- **UNAVAILABLE/AMBIGUOUS:** behavior is unsupported, undocumented on the installed software, or inconsistent. Meimad uses the selected one-time generic hook and does not treat an intrinsic caller identity as available.

The architecture choice for the unavailable/ambiguous outcome is now the one-time generic hook. The commissioning cases remain useful for documenting controller behavior, but an intrinsic caller-program variable is no longer required for release identity: the protected call receives the immutable six-digit identity stored for that exact approved release. Physical proof of argument handling, protected storage, arithmetic, failure alarms, cleanup, DPRNT, Reset, and power-cycle behavior remains a hard gate.

The independently reproducible response algorithm, public vectors, non-deployable protected-program layout, and additional arithmetic/input acceptance checks are specified in [Haas setup-verification response algorithm](haas-verification-response-algorithm.md). Passing its .NET tests or standalone PowerShell calculator is not CNC acceptance.

No PASS is valid from simulator output, public documentation, Q500 alone, or unit tests. The reviewed physical evidence, exact controller version, approved mappings, reset/power-cycle results, and sign-off must be appended here before changing the implementation-plan status.
