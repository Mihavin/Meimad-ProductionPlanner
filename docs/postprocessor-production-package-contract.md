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
DPRNT[PART=[[MEIMAD:PART_NAME]]]
DPRNT[OP=[[MEIMAD:OPERATION_NAME]]]
[[MEIMAD:EVENT_CONTEXT]]
```

If a control family uses another equivalent event output mechanism, use that mechanism with the same ownership model.

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

## 6. Machine-dependent composition

### CNC + Server Verification Enabled

At minimum:

1. package-specific runnable NC derived from the exact released NC source;
2. finalized current Tool Table / Tool Offset Table artifact or explicitly selected supported source mode;
3. unique package-specific Offset Loader for the existing approved verification protocol.

The runnable NC has all placeholders resolved. Verification hooks are present only because the assigned Machine has Server Verification enabled.

### CNC + Server Verification Disabled

At minimum:

1. package-specific runnable NC;
2. finalized current Tool Table / Tool Offset Table artifact or explicitly selected supported source mode.

No active Server Verification hook and no executable verification Offset Loader are generated.

### Manual Machine

Do not generate CNC verification code or an executable CNC Offset Loader. Do not invent an NC requirement for a manual process. Package the applicable human-readable/manual setup/tool artifacts already represented by the model.

## 7. Connectivity is delivery capability, not verification policy

Network connectivity controls available delivery methods. It does not decide whether Server Verification is enabled.

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
