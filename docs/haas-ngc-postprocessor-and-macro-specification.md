<<<<<<< HEAD
# Haas NGC postprocessor and Production Package specification

**Active protocol:** canonical Production Package protocol v2
**Audience:** SolidCAM/Cimatron postprocessor writers and CNC controls reviewers

The postprocessor is server-blind. It writes normal deterministic Haas cutting
code and exact Meimad placeholders. It does not contact the Planner, select a
Machine, create a package, assign an NC identity, or copy Part/Operation names
from CAM fields into Meimad metadata.

The authoritative contract is
[`postprocessor-production-package-contract.md`](postprocessor-production-package-contract.md).

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
- any unresolved required token in generated runnable NC.

## Compatibility and commissioning

The former exact `(MEIMAD PACKAGE VERIFY/CYCLE ... V1)` format remains a named
compatibility parser for immutable historical releases. Do not emit it from a
new or edited postprocessor. Existing `.docx` and dated machine-test documents
are historical evidence, not current postprocessor instructions.

Generated Haas verification and Offset Loader code still requires the existing
Machine-specific review and bounded no-motion commissioning. This document does
not declare any protected macro or controller interlock commissioned.
=======
# Haas NGC PostProcessor guide for Meimad Production Planner

**Audience:** Haas NGC post writers, especially SolidCAM GPPL and Cimatron GPP/GPP2 developers  
**Contract status:** Authoritative target contract for new canonical NC releases  
**Updated:** 2026-09-02

> **PostProcessor = deterministic Haas NC + stable placeholders.**  
> **Production Package Creator = authoritative placeholder resolution + Machine-specific transformation.**

The PostProcessor is server-blind. It must generate ordinary Haas cutting code and
stable Meimad tokens, but it must never read Planner data or write authoritative
Part, Operation, Machine, Run, Package, release, verification, creator, or timestamp
values.

This guide explains both sides of the boundary:

1. what the PostProcessor must emit in the immutable canonical NC release;
2. what the Server-generated runnable Haas file must contain for NC verification and
   cycle-based part counting.

The PostProcessor owns only item 1. The Production Package Creator owns item 2.

## 1. The three artifacts are different

| Artifact | Created by | Purpose |
|---|---|---|
| Canonical NC release | CAM PostProcessor | Immutable cutting program with stable Meimad placeholders. |
| Runnable package NC | Production Package Creator | Machine-specific copy with authoritative values and required verification/counting code. |
| Offset Loader | Production Package Creator | Package-specific setup/verification artifact when the assigned Machine requires it. |

Never edit the canonical release to make a package. Its exact bytes and SHA-256 remain
immutable; the runnable NC and Offset Loader receive their own artifact identities and
hashes.

## 2. Canonical placeholder grammar

Every Meimad token uses this exact grammar:

```text
[[MEIMAD:<KEY>]]
```

Token formatting rules:

- ASCII characters only inside the token.
- Use uppercase key names exactly as documented.
- Do not add spaces inside the token.
- Do not split a token across lines.
- Do not prefix a standalone executable insertion token with an `N` sequence number.
- Do not invent default values such as `UNKNOWN`, the CAM Part Name, the O-number, or
  the file name.
- Do not use fuzzy comments for integration. Package Creator parses exact tokens.
- A token is not a macro variable and is not valid runnable Haas code. It must be
  resolved or deliberately removed before package activation.

## 3. Required Haas source-template layout

A new Haas canonical release must use the following structure:

```gcode
%
O01500
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(MACHINE_ID: [[MEIMAD:MACHINE_ID]])
(POSTPROCESSOR_ID: [[MEIMAD:POSTPROCESSOR_ID]])
(PRODUCTION_RUN_ID: [[MEIMAD:PRODUCTION_RUN_ID]])
(PRODUCTION_PACKAGE_ID: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
(NC_RELEASE_ID: [[MEIMAD:NC_RELEASE_ID]])
[[MEIMAD:VERIFICATION_HOOK]]

G17 G40 G49 G80 G90
T1 M06
G54

[[MEIMAD:EVENT_CONTEXT]]
M03 S2500
G00 X0. Y0.
G01 Z-2. F100.
G00 Z50.
M05
[[MEIMAD:EVENT_CONTEXT]]

M30
%
```

The first `EVENT_CONTEXT` occurrence is the physical-cycle start insertion point.
The second is the successful physical-cycle end insertion point. Version 1 uses their
order to define the roles.

### Multiplicity

| Token | Canonical Haas rule |
|---|---|
| `PART_NAME` | Exactly once in the Meimad header. |
| `OPERATION_NAME` | Exactly once in the Meimad header. |
| `MACHINE_ID` | Exactly once. |
| `POSTPROCESSOR_ID` | Exactly once. |
| `PRODUCTION_RUN_ID` | Exactly once. |
| `PRODUCTION_PACKAGE_ID` | Exactly once. |
| `NC_RELEASE_ID` | Exactly once. |
| `VERIFICATION_HOOK` | Exactly once, before the first executable Haas block. |
| `EVENT_CONTEXT` | Either absent as a pair-disabled mode, or exactly twice in start/end order. |

`OFFSET_LOADER_RELEASE_ID`, the six-digit NC verification identity, nonce, macro
version, response code, event sequence variable, and protected program numbers belong
to the concrete package. The Haas post does not place or calculate them independently;
Package Creator inserts them through the verification/event expansions and generated
Offset Loader.

## 4. Part Name and Operation Name are always placeholders

Do not output a CAM or programmer-entered value in a Meimad identity field, even when
it appears correct:

```gcode
(PART: 30P450025601-001)       (WRONG IN CANONICAL RELEASE)
(OPERATION: OP20 FINISH)       (WRONG IN CANONICAL RELEASE)
```

Use:

```gcode
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
```

Package Creator resolves both from Meimad master data. This prevents a typo, stale CAM
property, renamed Operation, or copied CAM project from disagreeing with the Server.

The O-number and file name are locators only. They never identify the Part.

## 5. Verification insertion point

`[[MEIMAD:VERIFICATION_HOOK]]` must be before the first executable block. Only these
items may precede it:

- `%`;
- the `O` program header;
- blank lines;
- full-line comments containing placeholders or human-readable non-authoritative notes.

Safety codes, tool calls, macro calls, spindle commands, and motion are executable and
must not precede the token.

The PostProcessor must not emit any of the following:

```gcode
G65 P9001 ...
G65 P9002 ...
(MEIMAD VERIFY V1)
DPRNT[MEIMAD/V/1/EVENT/...]
```

It must also not output an Offset Loader, protected variable number, verification
timeout, Machine IP/MAC, tablet value, nonce, response, release token, or sequence
counter.

For a verification-enabled Machine, Package Creator currently expands the insertion
point into a call equivalent to:

```gcode
G65 P9002 A483921. (MEIMAD VERIFY V1)
```

The program number and six-digit identity above are examples only. They are selected
and bound by the Server for the exact package; the post must not hard-code them.

For a verification-disabled Machine, Package Creator removes the insertion token and
must leave no active Meimad verification code. Temporary network loss must not silently
change this policy.

## 6. Verification lifecycle the generated NC must support

The Server accepts verification only for the exact current binding of Production Run,
Machine, NC release/identity, Offset Loader release, nonce, and macro version.

```text
Offset Loader completed -> ARMED -> first main NC start -> PENDING -> SUCCEEDED
```

- `ARMED` has no timeout.
- The first intended main NC start emits the verification-request evidence and starts
  the configured pending timeout. The accepted product requirement is 120 seconds.
- Successful verification is reused for later starts of the same exact setup binding.
- A newly completed Offset Loader supersedes an earlier armed, pending, or successful
  binding.
- Event sequence numbers are diagnostic evidence. A gap, reset, wrap, duplicate, or
  out-of-order value is retained as an anomaly but is not identity authority.
- Machine recognition by configured Planner Machine ID, fixed IP, and MAC does not by
  itself prove that the NC or Offset Loader is valid.

## 7. Physical-cycle markers and part counting

The two `EVENT_CONTEXT` tokens must surround exactly one complete physical program
cycle:

```gcode
[[MEIMAD:EVENT_CONTEXT]]
(ALL CUTTING REQUIRED FOR ONE PHYSICAL CYCLE)
[[MEIMAD:EVENT_CONTEXT]]
```

Placement rules:

1. START belongs immediately before work that begins one physical cycle.
2. END belongs only on the common successful path after the entire physical cycle has
   completed.
3. END must execute before `M30`, `M99`, or any successful return that leaves the
   main cycle.
4. Alarm, reset, optional-stop, restart, or failure paths must not reach END as though a
   part completed.
5. Do not put a pair around every tool, CAM procedure, operation, or subprogram.
6. A restart that reaches a new START before a valid END interrupts the previous
   attempt; it does not count a part.
7. An END without a matching START is retained as an anomaly and does not increment
   production.
8. If one NC cycle produces several outputs, use one START/END pair around the atomic
   cycle. The Server increments each declared output by its immutable
   `quantityPerCycle`; do not emit one pair per output part.

Package Creator expands the first and second tokens into commissioned V10 `CST` and
`CEN` DPRNT blocks. The Server increments the completed-cycle count only after a
matched accepted `CEN`. A duplicate event identity is idempotent and cannot count the
same cycle twice.

Illustrative generated event lines are:

```text
MEIMAD/V/1/EVENT/CST/ID/NC-483921-S-12345/SEQ/41/MACROVERSION/10/PROGRAM/483921
MEIMAD/V/1/EVENT/CEN/ID/NC-483921-E-67890/SEQ/42/MACROVERSION/10/PROGRAM/483921
```

The Package Creator, not the post, produces these values.

## 8. Server wire-format constraints

Generated DPRNT records are strict:

```text
MEIMAD/V/1/EVENT/<CODE>/ID/<EVENT_ID>/SEQ/<N>/MACROVERSION/<N>
[/RUN/<RUN_ID>][/PROGRAM/<PROGRAM_ID>][/OFFSETRELEASE/<TOKEN>][/NONCE/<NONCE>]
```

Rules enforced by the Server:

- maximum line length: 512 ASCII bytes;
- only uppercase `A-Z`, digits, `/`, and `-` in the wire record;
- required field order: `V`, `EVENT`, `ID`, `SEQ`, `MACROVERSION`;
- optional order: `RUN`, `PROGRAM`, `OFFSETRELEASE`, `NONCE`;
- event IDs contain 1–128 characters and start with an uppercase letter or digit;
- verification evidence requires both six-digit `OFFSETRELEASE` and `NONCE`;
- cycle events must carry a resolvable `PROGRAM` identity;
- final package NC is ASCII with CRLF line endings.

These are Package Creator/macro-generation rules. The post writer preserves the
placeholder locations and must not duplicate the wire protocol.

## 9. Complete examples

### Correct canonical NC

```gcode
%
O01500
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(MACHINE_ID: [[MEIMAD:MACHINE_ID]])
(POSTPROCESSOR_ID: [[MEIMAD:POSTPROCESSOR_ID]])
(PRODUCTION_RUN_ID: [[MEIMAD:PRODUCTION_RUN_ID]])
(PRODUCTION_PACKAGE_ID: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
(NC_RELEASE_ID: [[MEIMAD:NC_RELEASE_ID]])
[[MEIMAD:VERIFICATION_HOOK]]
G17 G40 G49 G80 G90
T1 M06
G54
[[MEIMAD:EVENT_CONTEXT]]
M03 S2500
G01 X10. Y20. F300.
M05
[[MEIMAD:EVENT_CONTEXT]]
M30
%
```

### Wrong: CAM identity leaked into the canonical release

```gcode
(PART: PART-100)
(OPERATION: OP10)
```

### Wrong: verification insertion is late

```gcode
G90
[[MEIMAD:VERIFICATION_HOOK]]
```

### Wrong: permanently active Machine-specific code

```gcode
G65 P9002 A483921. (MEIMAD VERIFY V1)
```

### Wrong: only one cycle boundary

```gcode
[[MEIMAD:EVENT_CONTEXT]]
M30
```

### Wrong: one count per tool

```gcode
[[MEIMAD:EVENT_CONTEXT]]
T1 M06
[[MEIMAD:EVENT_CONTEXT]]
[[MEIMAD:EVENT_CONTEXT]]
T2 M06
[[MEIMAD:EVENT_CONTEXT]]
```

## 10. SolidCAM GPPL pattern

Use the installed post family's existing fatal-error mechanism. Emit the header tokens
from the main-file opening path, after `%`/`O` output and before normal start codes.

```text
@start_of_file
    call @usr_sof_character
    call @usr_sof_progname
    call @usr_meimad_header_and_hooks
    call @usr_sof_gmcodes
endp

@usr_meimad_header_and_hooks
    {nl,'(PART: [[MEIMAD:PART_NAME]])'}
    {nl,'(OPERATION: [[MEIMAD:OPERATION_NAME]])'}
    {nl,'(MACHINE_ID: [[MEIMAD:MACHINE_ID]])'}
    {nl,'(POSTPROCESSOR_ID: [[MEIMAD:POSTPROCESSOR_ID]])'}
    {nl,'(PRODUCTION_RUN_ID: [[MEIMAD:PRODUCTION_RUN_ID]])'}
    {nl,'(PRODUCTION_PACKAGE_ID: [[MEIMAD:PRODUCTION_PACKAGE_ID]])'}
    {nl,'(NC_RELEASE_ID: [[MEIMAD:NC_RELEASE_ID]])'}
    {nl,'[[MEIMAD:VERIFICATION_HOOK]]'}
endp
```

At the approved whole-cycle boundary:

```text
{nl,'[[MEIMAD:EVENT_CONTEXT]]'}
    ; existing output for one complete physical cycle
{nl,'[[MEIMAD:EVENT_CONTEXT]]'}
```

Call the header routine once per output file, not once per job, tool, procedure, or
subprogram. If one CAM project creates three separately released main programs, each
file receives the complete deterministic placeholder layout. Do not request a
Server-generated NC identity from the programmer.

## 11. Cimatron GPP/GPP2 pattern

Write literal tokens in `BEGINNING OF TAPE`, after the `O` header and before the
first Haas command:

```text
BEGINNING OF TAPE:
    OUTPUT "% " \J "O" PGN ;
    OUTPUT $ " (PART: [[MEIMAD:PART_NAME]])" ;
    OUTPUT $ " (OPERATION: [[MEIMAD:OPERATION_NAME]])" ;
    OUTPUT $ " (MACHINE_ID: [[MEIMAD:MACHINE_ID]])" ;
    OUTPUT $ " (POSTPROCESSOR_ID: [[MEIMAD:POSTPROCESSOR_ID]])" ;
    OUTPUT $ " (PRODUCTION_RUN_ID: [[MEIMAD:PRODUCTION_RUN_ID]])" ;
    OUTPUT $ " (PRODUCTION_PACKAGE_ID: [[MEIMAD:PRODUCTION_PACKAGE_ID]])" ;
    OUTPUT $ " (NC_RELEASE_ID: [[MEIMAD:NC_RELEASE_ID]])" ;
    OUTPUT $ " [[MEIMAD:VERIFICATION_HOOK]]" ;
```

Do not place these lines in beginning-of-procedure, beginning-of-toolpath, drilling,
milling, or tool-change blocks.

## 12. PostProcessor validation checklist

- [ ] The post works without Meimad Server access.
- [ ] Same CAM/post inputs produce the same canonical placeholder layout.
- [ ] Part and Operation Meimad fields contain tokens, never CAM-entered values.
- [ ] Every required unique token occurs exactly once.
- [ ] `VERIFICATION_HOOK` precedes the first executable Haas block.
- [ ] `EVENT_CONTEXT` is absent as a complete pair-disabled mode or occurs exactly
      twice around one complete physical cycle.
- [ ] The source contains no active Meimad `G65`, DPRNT verification, or Offset Loader.
- [ ] No package, Machine, Run, release, nonce, response, timeout, timestamp, creator,
      or hash value is invented by the post.
- [ ] Split output emits the complete token layout once in every separately released
      main file.
- [ ] The final posted bytes—not an earlier preview—are submitted as the immutable
      canonical release.

## 13. Current implementation rollout warning

The authoritative placeholder contract is
[postprocessor-production-package-contract.md](postprocessor-production-package-contract.md),
and the implementation task is
[../TASKS_FOR_CODEX_PRODUCTION_PACKAGE.md](../TASKS_FOR_CODEX_PRODUCTION_PACKAGE.md).

At the time of this update, the current Server parser/transformer still accepts the
legacy source marker:

```text
(MEIMAD PACKAGE VERIFY V1 NCID=dddddd)
```

and the legacy cycle START/END comments. It does not yet implement the complete
`[[MEIMAD:<KEY>]]` schema in this guide. Therefore:

- this document defines the new PostProcessor target;
- do not deploy a post that emits the new schema against the old package builder;
- complete and verify the Production Package Creator task first;
- do not restore CAM Part/Operation values or hard-coded verification as a temporary
  workaround;
- old released files remain historical/compatibility input under an explicit migration
  policy, not the format for new canonical releases.

## References

- [PostProcessor -> Production Package Creator contract](postprocessor-production-package-contract.md)
- [Production Package implementation task](../TASKS_FOR_CODEX_PRODUCTION_PACKAGE.md)
- [Production Run architecture](production-run-architecture.md)
- [Haas verification response algorithm](haas-verification-response-algorithm.md)
- [CNC commissioning checklist](cnc-commissioning-checklist.md)
>>>>>>> 427a87384b0d9d221b6cea2db8b2bec2d9c7b154
