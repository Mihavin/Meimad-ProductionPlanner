# Meimad NC Postprocessor -> Production Package Creator Contract

**Status:** Authoritative product architecture  
**Date:** 2026-09-01

## Non-negotiable principle

The postprocessor is **server-blind**. It generates deterministic NC from CAM data and emits stable Meimad placeholders only. It does not resolve Meimad identity/context values.

The server-side **Production Package Creator** is the only component that resolves those placeholders from Meimad master data and creates the package-specific machine-ready NC.

This rule explicitly includes **Part Name** and **Operation Name**. Even when CAM or the postprocessor technically has text that looks like those values, it must not be trusted as authoritative Meimad data. This avoids typos, stale CAM metadata, naming drift, and conflicts with server master data.

Canonical rule:

> **Postprocessor = deterministic NC + stable placeholders.**  
> **Production Package Creator = authoritative placeholder resolution + Machine-specific transformation + immutable package.**

Do not move server/master-data responsibility back into the postprocessor in future changes.

## 1. Responsibility boundary

### Postprocessor must

- Generate machine/post-specific cutting code from CAM input.
- Emit deterministic Meimad placeholders at protocol-defined locations.
- Preserve the placeholder grammar and required multiplicity.
- Be able to run with no Meimad Server connection, credentials, database, or network dependency.

### Postprocessor must not

- Read Meimad Server or Production Planner data.
- Resolve `PART_NAME`, `OPERATION_NAME`, Machine identity, Production Run identity, Production Package identity, release identity, verification challenge/context, creator, or server timestamp.
- Trust programmer-entered CAM names as authoritative Meimad identity.
- Decide which concrete Machine was assigned by Planning.
- Generate the final Production Package.

### NC release / Server must

- Store the canonical released NC immutably.
- Validate required placeholder structure at release time or before package creation.
- Keep the NC release identity separate from generated package-artifact identity.

### Production Package Creator must

- Load authoritative Operation, Case/Part, Machine, NC release, Tool/Offset release/source, and Machine-capability data from Meimad.
- Resolve all Meimad identity/context placeholders.
- Apply Machine-specific verification policy.
- Generate a package-specific machine-ready NC copy.
- Generate a package-specific Offset Loader only when the assigned Machine requires it.
- Write the package manifest, hashes/checksums, actor and server timestamp.
- Validate all artifacts and activate the package atomically.
- Never modify the canonical released NC in place.

## 2. Canonical released NC

The released NC is a **source release**, not necessarily the final runnable file. It preserves the exact CAM/post output plus stable Meimad integration markers. Package creation derives a separate machine-ready copy while retaining the immutable source release identity.

The post may of course output genuine CAM/post data such as geometry, motion, feeds, speeds, tool calls, work offsets, canned cycles, and control-specific syntax. The placeholder rule applies to **Meimad identity/context/server-owned values**, not to normal machining code.

All identity/context values in the canonical NC are placeholders, including Part Name and Operation Name.

A release is invalid for Production Package creation if required placeholders are missing, malformed, ambiguous, or duplicated where uniqueness is required.

## 3. Placeholder grammar

Use explicit machine-readable tokens. Canonical grammar:

```text
[[MEIMAD:<KEY>]]
```

Package Creator must parse tokens structurally. Do not implement arbitrary free-text search/replace.

Initial keys include:

| Placeholder | Final authority | Purpose |
|---|---|---|
| `[[MEIMAD:PART_NAME]]` | Case/Part master data | NC header / event metadata |
| `[[MEIMAD:OPERATION_NAME]]` | Operation master data | NC header / event metadata |
| `[[MEIMAD:PRODUCTION_RUN_ID]]` | Server production context | Event correlation |
| `[[MEIMAD:PRODUCTION_PACKAGE_ID]]` | Package Creator | Package identity |
| `[[MEIMAD:MACHINE_ID]]` | Planner-assigned Machine | Machine binding |
| `[[MEIMAD:NC_RELEASE_ID]]` | Exact immutable NC release | Audit/verification binding |
| `[[MEIMAD:OFFSET_LOADER_RELEASE_ID]]` | Generated package Offset Loader release | Verification binding when applicable |
| `[[MEIMAD:EVENT_CONTEXT]]` | Package Creator | Deterministic DPRNT/event correlation block |
| `[[MEIMAD:VERIFICATION_HOOK]]` | Package Creator policy transformation | Deterministic verification insertion point |
| `[[MEIMAD:CYCLE_START]]` | Package Creator | Physical-cycle-start part-counting event; required in every new template |
| `[[MEIMAD:CYCLE_END]]` | Package Creator | Physical-cycle-end part-counting event; required in every new template |

Protocol v2 multiplicity is explicit: `PART_NAME` and `OPERATION_NAME` are
repeatable but required at least once. `CYCLE_START` and `CYCLE_END` are
required exactly once in every template (`production_package_cycle_marker_required`
when both are absent; the check runs at package build, so an older release without the pair must be re-released). Every other key in the table is required exactly once for a canonical
CNC template. `EVENT_CONTEXT` and `VERIFICATION_HOOK` occupy standalone lines,
and `VERIFICATION_HOOK` precedes the first executable block. Keys are
uppercase and exact; unknown keys, malformed delimiters, and invalid
duplicates fail closed.

`CYCLE_START` and `CYCLE_END` must both be present or both be absent — one
without the other fails closed with `production_package_cycle_marker_unpaired`.
When present, each occupies its own standalone line, `CYCLE_START` must
precede `CYCLE_END` (`production_package_cycle_marker_order_invalid`
otherwise), and each may appear at most once. Unlike `VERIFICATION_HOOK`, the
pair belongs *inside* the executable body, surrounding exactly one complete
physical cutting cycle — not before the first executable block. Package
Creator expands the pair into the same wire-format `DPRNT[MEIMAD/V/1/EVENT/
CST/...]` / `.../CEN/...]` part-counting events used by the legacy
`(MEIMAD PACKAGE CYCLE START/END V1)` markers, and — matching that same legacy
behavior — only when the assigned Machine has Server Verification enabled; on
a verification-disabled Machine both markers are silently removed and no
count-affecting DPRNT is emitted. There is currently no non-verification path
for automated part counting.

`MACHINE_ID`, `NC_RELEASE_ID`, `PRODUCTION_RUN_ID`, and `PRODUCTION_PACKAGE_ID`
resolve to short unique 6-digit numbers meant to be read and typed by hand at
the control, not to any internal identifier string. `NC_RELEASE_ID` reuses the
release's existing verification identity token; `MACHINE_ID` reuses the
Machine's existing short number; `PRODUCTION_RUN_ID` and
`PRODUCTION_PACKAGE_ID` get their own dedicated 6-digit numbers generated the
same way. Do not assume any of these four match the length or format of the
Server's internal GUID identifiers.

The former `(MEIMAD PACKAGE VERIFY/CYCLE ... V1)` syntax is protocol v1. It is
parsed only by a separate exact compatibility path so already immutable
historical releases remain buildable. It is not emitted by current
postprocessors and is never detected by fuzzy text matching. Protocol v2 NC
identity is assigned by the Server at immutable release publication; it is not
postprocessor or CAM input.

The exact key list may evolve with protocol versions. The ownership rule does not: if a value represents Meimad identity, current planning context, package context, verification context, or server master data, the post emits a placeholder and Package Creator resolves it.

## 4. Where placeholders go in the NC

### Header identity block

Example canonical source:

```text
%
O1500
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(MACHINE: [[MEIMAD:MACHINE_ID]])
(NC_RELEASE: [[MEIMAD:NC_RELEASE_ID]])
(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
```

The control/post family may adapt surrounding comment syntax, but the Meimad token itself must remain parser-safe and unambiguous.

### DPRNT / event context

```text
[[MEIMAD:EVENT_CONTEXT]]
DPRNT[[[MEIMAD:PART_NAME]]]
```

The Server takes machine-side Part identity only from a bare part-number-shaped DPRNT line (uppercase letters and digits with at least one `-` or `.` separator, not ending `.CNC`). A prefixed form such as `DPRNT[PART=...]` is ignored, and `OPERATION_NAME` belongs in a `( )` comment, never in a DPRNT. On FANUC-family controls the DPRNT lines sit between the writer's own `POPEN` and `PCLOS`.

Package Creator expands `EVENT_CONTEXT` itself, on verification-enabled and verification-disabled Machines alike, in the NC dialect configured on the assigned Machine (`ncDialect`): a `DPRNT[MEIMAD/V/2/CONTEXT/...]` statement for `HAAS_NGC`, `FANUC_MACRO_B`, and `MAZAK_MATRIX_EIA`, or a `PUT 'MEIMAD/V/2/CONTEXT/...'` statement followed by `WRITE C` for `OKUMA_OSP`. The same dialect selects the verification hook (`G65 P9002 A<nc>.` or `CALL O9002 PA=<nc>`), the cycle-event blocks, and the Offset Loader program (`offset-loader/O01990.nc` or `offset-loader/O1990.MIN`). The postprocessor never rewrites Server-generated blocks and does not choose the dialect. Per-control examples are in [`nc-postprocessor-and-macro-specification.md`](nc-postprocessor-and-macro-specification.md).

### Part counting (required)

```text
[[MEIMAD:CYCLE_START]]
(normal CAM-generated tools, motion, feeds, speeds and cycles for one complete
physical part cycle)
[[MEIMAD:CYCLE_END]]
```

Place `CYCLE_START` immediately before the work that begins one physical cycle and
`CYCLE_END` only on the common successful path after that cycle fully completes —
before `M30`/`M99`/any successful return, never on an alarm/reset/optional-stop/
failure path. Every template contains exactly one pair; a template without it fails
package creation with `production_package_cycle_marker_required`.

Package Creator expands the pair into the wire-format `CST`/`CEN` events for every Machine
whose enabled CNC connection reads DPRNT (source TCP, FILE or FTP) and for every
verification-enabled Machine; the sequence variable comes from the Machine's verification
configuration when one exists (enabled or not), else from the dialect default. A Machine
without a DPRNT connection gets the markers removed. The manifest records
`partCountingEnabled`, `partCountingDprntSource`, `partCountingEventSequenceVariable` and
`partCountingVariableFromConfiguration`.

### Verification insertion point

```text
(MEIMAD VERIFICATION INSERTION POINT)
[[MEIMAD:VERIFICATION_HOOK]]
```

The post does not decide whether Server Verification is enabled. Package Creator either expands the marker into the approved verification hook/block or removes it cleanly.

## 5. Production Package Creator algorithm

1. Load the Operation and its concrete Planner-assigned Machine. Reject package creation when a required concrete Machine assignment is absent.
2. Resolve the exact current immutable NC release when the Machine/Operation requires NC.
3. Resolve the exact current Tool Table / Tool Offset Table source required by the package mode, including an explicitly supported Manual/Dummy Tool Offsets mode where configured.
4. Load Machine capabilities: CNC vs Manual, Server Verification enabled/disabled, network/delivery capability, post/control compatibility, and any required package-generation capability.
5. Parse and validate the canonical NC placeholder structure. Reject missing, malformed, unknown-required, or invalidly duplicated placeholders.
6. Create a new `ProductionPackageId` and package-build context.
7. Create a package-specific NC copy. Resolve identity/context placeholders only from authoritative Meimad server/master data.
8. For a CNC with **Server Verification Enabled**, inject the approved verification hooks and generate a new package-specific Offset Loader bound to the exact Operation, Machine, NC release, package, and verification context.
9. For a CNC with **Server Verification Disabled**, remove verification markers and produce a runnable NC containing no active Meimad Server-verification code and no executable verification Offset Loader.
10. For a **Manual Machine**, do not invent CNC NC/verification artifacts. Package only meaningful manual setup/tool artifacts represented by the model.
11. Write the immutable manifest including creator/user identity, server timestamp, exact bound releases/configuration/source modes, generated artifact identities, and hashes/checksums.
12. Validate every generated artifact, write them to server-managed package storage, and activate the package atomically. A partial or failed build must never become current or produce `Ready for Setup`.

**Measured tool offsets (schema v79).** When the released Tool Table has tool rows and the package mode is `MEASURED`, the build reads the latest Tool Room tool preparation of the Operation on the assigned Machine and requires a measured length, a measured diameter and an offset number for every required released tool (`production_package_tool_measurements_missing`; duplicate offset numbers fail with `production_package_tool_offset_number_duplicate`). The measurements are written as the `TOOL_OFFSETS` artifact `tool-offsets/tool-offsets.json` and, in the Machine's `ncDialect`, as NC offset-input lines: `G10 L10 P<n> R<length>` / `L11 R0.` / `L12 R<radius or diameter>` / `L13 R0.` (memory C) for milling on Haas NGC, FANUC macro B and Mazak Matrix, `G10 P1<nnnn> X<diameter> Z<length>` for FANUC turning, `VTOFH[n]`/`VTOFD[n]` or `VTOFX[n]`/`VTOFZ[n]` for Okuma OSP. The Machine's `toolDiameterOffsetKind` decides whether the D value is half of the measured diameter (`RADIUS`, default) or the whole diameter. With Server Verification enabled the lines precede the challenge call inside the package-specific Offset Loader; with verification disabled they form the separate `TOOL_OFFSET_PROGRAM` artifact (`tool-offsets/O01991.nc` or `tool-offsets/O1991.MIN`). Haas NGC and Mazak turning have no supported input syntax: the JSON artifact is still produced, the manifest says `toolOffsetsLoadedByProgram: false`, and the loader comment tells the setupist to enter the sheet. `Manual / Dummy Tool Offsets` packages omit all of this. A newer saved preparation version makes the package stale exactly like a newer Tool Table release.

## 6. Machine-dependent composition

### CNC + Server Verification Enabled

At minimum:

1. package-specific runnable NC derived from the exact released NC source;
2. finalized current Tool Table / Tool Offset Table artifact or explicitly selected supported source mode;
3. unique package-specific Offset Loader for the existing approved verification protocol;
4. for a `MEASURED` package with released tool rows, the `TOOL_OFFSETS` sheet and the measured offset lines inside the Offset Loader (see section 5).

The runnable NC has all placeholders resolved. Verification hooks are present only because the assigned Machine has Server Verification enabled.

### CNC + Server Verification Disabled

At minimum:

1. package-specific runnable NC (with the part-counting cycle events when the Machine's CNC connection reads DPRNT);
2. finalized current Tool Table / Tool Offset Table artifact or explicitly selected supported source mode.

No active Server Verification hook and no executable verification Offset Loader are generated.

### Manual Machine

Do not generate CNC verification code or an executable CNC Offset Loader. Do not invent an NC requirement for a manual process. Package the applicable human-readable/manual setup/tool artifacts already represented by the model.

## 7. Connectivity is delivery capability, not verification policy

Network connectivity controls available delivery methods. It does not decide whether Server Verification is enabled. The CNC connection's DPRNT source does decide part counting: a Machine whose connection is enabled with a DPRNT source other than `NONE` receives the cycle events whether or not its verification is enabled.

- If the Machine has a supported direct network transfer path and is connected, direct send may be offered.
- File open/export/copy remains available for the normal shop-floor workflow.
- Do **not** silently disable configured Server Verification because a Machine is temporarily disconnected.
- If configured verification cannot actually be supported by the current Machine/infrastructure configuration, block package creation with a clear configuration error rather than silently creating a weaker package.

## 8. Immutability and invalidation

- Canonical released NC is immutable.
- Generated machine-ready NC has its own artifact hash and belongs to one Production Package.
- A Production Package is scoped to one exact Operation + assigned Machine.
- Package/Offset Loader artifacts must not become current for a different Operation merely because machining/tool data happen to match.
- A current package becomes stale/superseded when a materially bound input changes, including Machine assignment, NC release, Tool/Offset release or selected source mode, or verification-relevant Machine configuration.
- A new successful package build supersedes the prior current package for that Operation/Machine context while history remains audit evidence.
- Opening, viewing, copying, exporting, or sending a package does not change workflow state and does not change package authorship.

## 9. Validation requirements

- Token/grammar-based parsing only; no fuzzy matching.
- Unknown required placeholders fail closed unless a protocol version explicitly declares them optional.
- Placeholders declared unique must occur exactly once; repeatable placeholders must have declared multiplicity.
- `CYCLE_START`/`CYCLE_END` must occur together (both present exactly once, in that order); one without the other or out of order fails closed. Absence of both fails closed as well (`production_package_cycle_marker_required`).
- All required placeholders must be resolved or intentionally removed by a named transformation before a runnable CNC artifact can be activated.
- Verification-disabled runnable NC must contain no unresolved verification marker and no active Server-verification code.
- Verification-enabled runnable NC must contain the approved hook/version and exact current package correlation.
- Package Creator must record input release hashes and generated output hashes so the transformation is auditable.

## 10. Explicit anti-patterns

The following are prohibited:

- Postprocessor writes CAM Part Name because it "already knows it".
- Postprocessor writes Operation Name from a programmer-entered CAM field.
- Postprocessor contacts Meimad Server during posting.
- Postprocessor generates package ID, current Machine identity, verification challenge/context, creator, or server timestamp.
- Package Creator guesses injection locations by searching arbitrary comments.
- Package Creator overwrites the canonical NC release.
- Verification is permanently active in every released NC regardless of Machine configuration.
- Network disconnection silently disables configured verification.
- One generated Offset Loader is reused as current for several Operations/packages.
- Package creation succeeds with unresolved required placeholders.

## 11. Acceptance criteria

A compliant implementation must prove at least:

- Same CAM/post inputs produce deterministic canonical NC and the same placeholder layout.
- Postprocessor operates correctly with no Meimad Server access.
- `PART_NAME` and `OPERATION_NAME` are placeholders in canonical released NC Meimad metadata locations.
- Release/package validation detects malformed or missing required placeholders.
- Package Creator gets final Part/Operation names and other context from Meimad server/master data, never from CAM identity text.
- Verification-enabled CNC package receives the approved hooks and unique package Offset Loader.
- Verification-disabled CNC package contains no active Server-verification hook and no verification Offset Loader.
- Manual Machine package does not acquire invented CNC artifacts.
- Package artifacts and manifest are immutable once current and retain exact bound release/configuration/hash identities.
- Changing a bound package input invalidates the previous package and requires a new build before `Ready for Setup` can again be true.

## 12. Rule for future Codex work

Do not reinterpret "the post knows this value" as permission to make it authoritative. The separation is intentional:

> **Postprocessor outputs deterministic machining code and stable placeholders. Package Creator owns authoritative resolution and package-specific transformation.**
