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
