# Haas VF-3 NGC active-program header technical spike

**Persistent CNC workflow mode variable: REMOVED.** **Protected temporary setup
verification variables: SUPPORTED** only after the per-Machine checklist in
`cnc-commissioning-checklist.md` passes.

## Decision

Automatic Bench identification is enabled only when Meimad reads a bounded header from the Haas-accessible Local Net Share and proves that the active program locator maps to exactly one readable machine-side file. MDC normally supplies an O-number. MTConnect may instead supply a filename-like `PROGRAM` value, which remains informational until a site test proves a unique header mapping. The filename is never a Part identifier. The shared `INcHeaderParser` extracts `PART` from that machine file, and only that parsed value may match planned work.

There is no server-release fallback. If the machine file is absent, unreadable, duplicated by O-number, or has no valid Part header, Meimad records an error/anomaly and does not start or switch a Bench.

## What Haas documents

- Haas MDC is a TCP server enabled/configured by Setting 143. Queries use uppercase `?Q###` plus newline and responses are comma-separated records. `Q500` returns the active program locator/status and Parts counter; it does not return NC header content. `Q600` reads a macro/system variable. `E` writes a writable variable and returns `!` or `?`. [Haas NGC Machine Data Collection](https://www.haascnc.com/service/online-manuals/next-gen-control-electrical---service-manual/ngc---machine-data-collection.html)
- Haas recommends extreme caution for variable writes. Meimad exposes no generic variable-write API and has removed the persistent Setup/Production workflow variable. The implemented Server-side protected setup-verification handshake permits temporary variables only after the controller-specific protected-program mechanism, ranges, reset/power-cycle behavior, and collision risks are bench-tested and recorded. [Haas NGC Machine Data Collection](https://www.haascnc.com/service/online-manuals/next-gen-control-electrical---service-manual/ngc---machine-data-collection.html)
- Haas Local Net Share makes CNC Machine Data and User Data available to a shop PC; Net Share is also used to transfer programs. Haas's public documentation does not state that the currently active program in MEMORY always has a uniquely addressable file in the share. [Haas wired/wireless networking](https://www.haascnc.com/service/troubleshooting-and-how-to/how-to/wired--wireless--network---ngc.html), [Haas NGC operator manual](https://www.haascnc.com/content/dam/haascnc/en/service/manual/operator/english---mill-ngc---operator%27s-manual---2025.pdf)

## Implemented read-only adapter

`LocalNetShareHaasProgramReader` runs on Meimad Server under the Windows Service account:

1. Read the active locator. MDC supplies an `Oxxxxx` number. MTConnect may supply a numeric filename such as `1500.CNC`; Meimad accepts that only when it finds that exact filename and its header contains the matching `O1500` number.
2. Require the same locator in the configured number of consecutive polls.
3. Enumerate the configured Haas share read-only.
4. Read at most the configured byte and line limits from candidate files (defaults: 32 KiB and 50 lines).
5. Match the O-number found inside each candidate header; for MTConnect filename locators, also match the exact filename.
6. Continue only when exactly one candidate exists.
7. Parse that candidate with the same `INcHeaderParser` used during server G-code release. The default parser accepts both `(PART: 30P283003300-002)` and the Meimad CAM form `O1500 (30P283003300-002_NC1)`.

The share credential field is a reference only. No password is stored in the Machine or Haas settings tables. Access is expected to be provisioned for the Server service account or by a future approved secret provider.

## MTConnect commissioning evidence (2026-08-23)

The configured VF-3SS agent returned HTTP 200 and MTConnect 1.2 XML from both root `/probe` and `/current`. Meimad's production reader and the full `/haas/test-mtconnect` API path were executed against that agent successfully. The feed reported one device (`VF-3SS`), `AVAILABLE`, controller mode `AUTOMATIC`, execution state, `PROGRAM=1500.CNC`, M30 counters, spindle observations, and Haas macro-range `Source` metadata. The adapter deliberately fetches unfiltered documents because this agent's XPath query behavior is not relied upon.

The probe also exposed macro-range metadata, but those observations are not workflow state. The previously inspected value has no Setup/Production meaning and changing it cannot create a Server workflow transition. This evidence may inform the later protected-verification technical spike, but it does not approve a variable range or challenge/response mapping.

## Required VF-3 validation before production enablement

The following items cannot be established from public documentation and must be recorded on the actual VF-3 NGC before `enabled = true`:

| Question | Required test | Current status |
|---|---|---|
| Which directory contains readable NC programs? | Record the exact UNC root and file location after copying/loading a controlled test program. | Not yet verified on VF-3 |
| Is a program in MEMORY exposed? | Select a known MEMORY program, read Q500, and locate the same O-number/header through the share. | Not documented; test required |
| Can active O-number map to a physical file? | Compare Q500 O-number with the bounded file-header read. | Adapter implemented; hardware proof required |
| Can the file be read while active/running? | Open it read-only during Setup and during a cycle. Confirm no controller warning or lock failure. | Test required |
| Are reads truly read-only? | Grant the service identity read/list only; verify monitoring still works and writes/deletes fail. | Deployment test required |
| Required SMB permissions? | Record share, NTFS/controller account, protocol version, and service identity. | Site-specific |
| Program loaded from USB? | Select/run from USB and determine whether a corresponding share file exists. | Unknown; fail closed if absent |
| Program from Remote Net Share? | Select/run from Remote Net Share and determine whether Local Share exposes the active bytes. | Unknown; fail closed if absent |
| Duplicate O-number? | Place two files containing the same O-number. Confirm Meimad reports ambiguity and does not start a Bench. | Simulator covered; hardware test required |

## Shop-floor acceptance procedure

Use a non-production test program whose filename differs from its header identity:

```text
filename: JOB_8372.NC
O1234
(PART: HAAS-SPIKE-PART-A)
```

1. Configure the VF-3 IP, explicitly select MDC or MTConnect as the read source, configure its port, the read-only share path, and a two-poll debounce. There is no persistent workflow-variable setting.
2. Use **Test Connection** for the selected provider. When commissioning MDC separately, use **Test MDC**; when commissioning MTConnect separately, use **Test MTConnect**.
3. Use **Test Net Share**; verify Program `O1234`, Part `HAAS-SPIKE-PART-A`, and the exact machine source path.
4. Select the program twice without running it. Confirm the planned Batch Operation starts once in `SETUP`.
5. Change an unrelated CNC variable between `0` and `1`. Confirm that no Setup, Production, QC, or cycle workflow event is created.
6. Increment the selected parts counter and confirm monitoring remains available without inventing workflow state.
7. Disconnect/reconnect networking and confirm the same Bench is reconciled without duplicate starts.
8. Repeat with no header, a conflicting header, USB, Remote Net Share, and duplicate O-number. Every unproven identity must fail closed.

Schema v50 now provides disabled-by-default Machine configuration, immutable Offset Loader identity, strict DPRINT framing/ingestion, and sequence anomaly storage so the spike can be run without hardcoded controller choices. The separate [protected setup-verification spike](haas-protected-verification-spike.md) must still prove protected O9000/custom-code behavior, temporary-variable ranges and cleanup, DPRINT delivery, active NC release identity, challenge/response independence, power-cycle safety, and sequence survival before verification is enabled. No protected program number or variable is approved by this header-read spike or by storing configuration in the Server.

Phase 1/2 real-machine Definition of Done remains pending until this procedure is completed and its exact VF-3 paths/permissions/results are appended here.
