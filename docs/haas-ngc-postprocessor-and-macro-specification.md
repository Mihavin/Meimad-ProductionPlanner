# Haas NGC postprocessor and Production Package specification

**Active protocol:** canonical Production Package protocol v2
**Audience:** SolidCAM/Cimatron postprocessor writers and CNC controls reviewers

The postprocessor is server-blind. It writes normal deterministic Haas cutting
code and exact Meimad placeholders. It does not contact the Planner, select a
Machine, create a package, assign an NC identity, or copy Part/Operation names
from CAM fields into Meimad metadata.

The authoritative contract is
[`postprocessor-production-package-contract.md`](postprocessor-production-package-contract.md).

> A newer, more detailed draft contract (adding a `POSTPROCESSOR_ID` token and other
> changes) exists at
> [`haas-ngc-postprocessor-and-macro-specification.v2-draft.md`](haas-ngc-postprocessor-and-macro-specification.v2-draft.md).
> That draft is **not implemented** by the current Production Package Creator — its own
> rollout warning says so. Do not write a postprocessor against it. `POSTPROCESSOR_ID` is
> not a recognized placeholder key; using it fails Production Package creation with
> `production_package_placeholder_unknown`. Use this document only.

## Required canonical block

Every new Haas CNC source template must contain this logical structure. Comment
wording around a token may vary, but the token spelling may not.

```gcode
%
O1500
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])
(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
(MACHINE: [[MEIMAD:MACHINE_ID]])
(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])
(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])
[[MEIMAD:VERIFICATION_HOOK]]
[[MEIMAD:EVENT_CONTEXT]]
G90 G17 G40 G49 G80
(normal CAM-generated tools, motion, feeds, speeds and cycles follow)
M30
%
```

`PART_NAME` and `OPERATION_NAME` may repeat when both header and event metadata
need them; each must appear at least once. Every other key above appears exactly
once. `VERIFICATION_HOOK` and `EVENT_CONTEXT` are standalone lines. The hook
must be before the first executable NC block.

Do not emit a real Part name, Operation name, Planner Machine ID, Run ID,
Package ID, release ID, user, timestamp, challenge, response, or verification
call. The Server owns all of those values.

`MACHINE_ID`, `NC_RELEASE_ID`, `RUN_ID` (via `PRODUCTION_RUN_ID`), and `PACKAGE_ID` (via
`PRODUCTION_PACKAGE_ID`) resolve to short unique 6-digit numbers meant to be read and typed
by hand at the control (e.g. `483921`), not to the Server's internal identifiers. Do not
assume these resolved values are the same length or format as any internal ID string.

## Part counting (optional)

Two additional keys enable automated part counting for an Operation:

```gcode
[[MEIMAD:CYCLE_START]]
(normal CAM-generated tools, motion, feeds, speeds and cycles for one complete
physical part cycle)
[[MEIMAD:CYCLE_END]]
```

Unlike every other key in this document, `CYCLE_START`/`CYCLE_END` are **optional** — an
Operation with no automated part counting omits both. If used, both must be present
exactly once, `CYCLE_START` must precede `CYCLE_END`, and each occupies its own standalone
line. One without the other, or out of order, fails Production Package creation
(`production_package_cycle_marker_unpaired` / `production_package_cycle_marker_order_invalid`).

Placement, unlike `VERIFICATION_HOOK`, is *inside* the executable body: `CYCLE_START`
immediately before the work that begins one physical cycle, `CYCLE_END` only on the common
successful path after that cycle fully completes — before `M30`/`M99`/any successful
return, never on an alarm/reset/optional-stop/failure path. Do not wrap every tool, CAM
procedure, or subprogram in its own pair; use one pair around the atomic cycle the Server
should count.

Package Creator expands the pair into the same wire-format part-counting `DPRNT` events
(`MEIMAD/V/1/EVENT/CST/...` / `.../CEN/...`) as the legacy `(MEIMAD PACKAGE CYCLE
START/END V1)` markers, and only when the assigned Machine has **Server Verification
enabled** — on a verification-disabled Machine both markers are silently removed and no
count-affecting event is emitted. There is currently no non-verification path for
automated part counting.

## SolidCAM example

In the SolidCAM post, print the literal strings; do not bind them to job fields:

```text
write_block('(PART: [[MEIMAD:PART_NAME]])')
write_block('(OPERATION: [[MEIMAD:OPERATION_NAME]])')
write_block('(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])')
write_block('(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])')
write_block('(MACHINE: [[MEIMAD:MACHINE_ID]])')
write_block('(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])')
write_block('(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])')
write_block('[[MEIMAD:VERIFICATION_HOOK]]')
write_block('[[MEIMAD:EVENT_CONTEXT]]')
```

Exact SolidCAM procedure names vary by post version. The required result is the
literal NC shown above. No HTTP, database, environment lookup, or Planner file
lookup belongs in the post.

## Cimatron example

Emit literal output records in the program-start section before executable
modal or motion blocks:

```text
{nl,'(PART: [[MEIMAD:PART_NAME]])'}
{nl,'(OPERATION: [[MEIMAD:OPERATION_NAME]])'}
{nl,'(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])'}
{nl,'(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])'}
{nl,'(MACHINE: [[MEIMAD:MACHINE_ID]])'}
{nl,'(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])'}
{nl,'(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])'}
{nl,'[[MEIMAD:VERIFICATION_HOOK]]'}
{nl,'[[MEIMAD:EVENT_CONTEXT]]'}
```

Do not substitute Cimatron `$PART_NAME`, procedure name, document name, or user
text for the Meimad name placeholders. Those CAM values may remain in unrelated
CAM comments, but they are never Meimad authority.

## What the Server generates

For a CNC with Server Verification enabled, Production Package Creator replaces
all identity tokens from Planner master data, expands `VERIFICATION_HOOK` into
the configured approved `G65 P9xxx Axxxxxx.` call, emits deterministic event
context, and generates a unique package-bound O01990 Offset Loader using the
existing verification protocol.

For a CNC with verification disabled, it resolves identity/context tokens,
removes the hook line, writes `NOT_APPLICABLE` for Offset Loader identity, and
generates no verification Offset Loader.

For Manual/Dummy Tool Offsets on a verification-enabled CNC, it generates the
same identity-bound verification Offset Loader but no measured offset payload or
G10 offset commands. The setupist enters real offsets manually.

## Validation failures

Package/release validation rejects:

- a missing required key;
- malformed `[[MEIMAD:...]]` syntax;
- an unknown key;
- duplicate unique keys;
- a hook not on a standalone line or after executable code;
- active Meimad verification logic embedded in the source;
- any unresolved required token in generated runnable NC;
- `CYCLE_START` without a matching `CYCLE_END` (or the reverse), or `CYCLE_END` before
  `CYCLE_START`.

## Compatibility and commissioning

The former exact `(MEIMAD PACKAGE VERIFY/CYCLE ... V1)` format remains a named
compatibility parser for immutable historical releases. Do not emit it from a
new or edited postprocessor. Existing `.docx` and dated machine-test documents
are historical evidence, not current postprocessor instructions.

Generated Haas verification and Offset Loader code still requires the existing
Machine-specific review and bounded no-motion commissioning. This document does
not declare any protected macro or controller interlock commissioned.

## Troubleshooting failures seen in practice

These two mistakes have each broken real Production Package creation for a released
postprocessor; check for them first when a release is rejected.

- **`production_package_placeholder_unknown: Unknown Meimad placeholder '<KEY>' on line
  N.`** — the source template contains a `[[MEIMAD:<KEY>]]` token that is not in the
  9-key set at the top of this document (most commonly `POSTPROCESSOR_ID`, which is not
  and has never been a valid key — see the draft-contract warning above). Fix the
  postprocessor macro to stop emitting it, then release a corrected NC. There is no
  server-side allow-list update that will make an invented key valid; the fix is always
  in the source template.
- **`production_package_placeholder_duplicate: Canonical NC template must contain
  exactly one [[MEIMAD:EVENT_CONTEXT]].`** — the template emits `EVENT_CONTEXT` twice
  (e.g. a start/end pair for cycle-boundary marking). The currently implemented protocol
  requires exactly one occurrence; it does not yet implement start/end cycle-boundary
  semantics (see the draft contract for that future design). Remove the extra
  occurrence — keeping either one is equivalent for the currently implemented protocol.

If a release keeps failing after a fix, use "View NC File (read-only)" or download the
release directly and `grep`/search for `[[MEIMAD:` to see every token and line number
actually present, rather than assuming the macro emits what its source code says — a
stale CAM export or wrong output folder can silently ship an older file.

## References

- [PostProcessor -> Production Package Creator contract](postprocessor-production-package-contract.md)
- [Production Package implementation task](../TASKS_FOR_CODEX_PRODUCTION_PACKAGE.md)
- [Production Run architecture](production-run-architecture.md)
- [Haas verification response algorithm](haas-verification-response-algorithm.md)
- [CNC commissioning checklist](cnc-commissioning-checklist.md)
