# Haas VF-3 NGC active-program header technical spike

## Decision

Automatic Bench identification is enabled only when Meimad reads a bounded header from the Haas-accessible Local Net Share and proves that the active MDC O-number maps to exactly one readable machine-side file. The filename is never a Part identifier. The shared `INcHeaderParser` extracts `PART` from that machine file, and only that parsed value may match planned work.

There is no server-release fallback. If the machine file is absent, unreadable, duplicated by O-number, or has no valid Part header, Meimad records an error/anomaly and does not start or switch a Bench.

## What Haas documents

- Haas MDC is a TCP server enabled/configured by Setting 143. Queries use uppercase `?Q###` plus newline and responses are comma-separated records. `Q500` returns the active program locator/status and Parts counter; it does not return NC header content. `Q600` reads a macro/system variable. `E` writes a writable variable and returns `!` or `?`. [Haas NGC Machine Data Collection](https://www.haascnc.com/service/online-manuals/next-gen-control-electrical---service-manual/ngc---machine-data-collection.html)
- Haas recommends extreme caution for variable writes and currently documents the NGC writable global range that includes `#10605`. Meimad therefore exposes no generic variable-write API: only the configured production variable may be written, only value `0`, and only through the audited Tool Table reset workflow. [Haas NGC Machine Data Collection](https://www.haascnc.com/service/online-manuals/next-gen-control-electrical---service-manual/ngc---machine-data-collection.html)
- Haas Local Net Share makes CNC Machine Data and User Data available to a shop PC; Net Share is also used to transfer programs. Haas's public documentation does not state that the currently active program in MEMORY always has a uniquely addressable file in the share. [Haas wired/wireless networking](https://www.haascnc.com/service/troubleshooting-and-how-to/how-to/wired--wireless--network---ngc.html), [Haas NGC operator manual](https://www.haascnc.com/content/dam/haascnc/en/service/manual/operator/english---mill-ngc---operator%27s-manual---2025.pdf)

## Implemented read-only adapter

`LocalNetShareHaasProgramReader` runs on Meimad Server under the Windows Service account:

1. Read `Q500` and normalize the active `Oxxxxx` locator.
2. Require the same locator in the configured number of consecutive polls.
3. Enumerate the configured Haas share read-only.
4. Read at most the configured byte and line limits from candidate files (defaults: 32 KiB and 50 lines).
5. Match the O-number found inside each candidate header.
6. Continue only when exactly one candidate exists.
7. Parse that candidate with the same `INcHeaderParser` used during server G-code release.

The share credential field is a reference only. No password is stored in the Machine or Haas settings tables. Access is expected to be provisioned for the Server service account or by a future approved secret provider.

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

1. Configure the VF-3 IP, Setting 143 MDC port, read-only share path, `#10605`/legacy `#605`, and a two-poll debounce.
2. Use **Test MDC**; record Q500 Program/Status/Parts.
3. Use **Test Net Share**; verify Program `O1234`, Part `HAAS-SPIKE-PART-A`, and the exact machine source path.
4. Select the program twice without running it. Confirm the planned Batch Operation starts once in `SETUP`.
5. Press Cycle Start repeatedly while the variable is `0`. Confirm it remains `SETUP`.
6. Change the configured macro from `0` to `1`. Confirm exactly one `BenchProductionStarted` event and one Production interval.
7. Increment the selected parts counter and confirm immutable `PartCompleted` events.
8. Disconnect/reconnect networking and confirm the same Bench is reconciled without duplicate starts.
9. Repeat with no header, a conflicting header, USB, Remote Net Share, and duplicate O-number. Every unproven identity must fail closed.

Phase 1/2 real-machine Definition of Done remains pending until this procedure is completed and its exact VF-3 paths/permissions/results are appended here.
