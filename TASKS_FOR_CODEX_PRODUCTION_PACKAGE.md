# TASK FOR CODEX — Implement canonical Production Package creation

**Status:** authoritative follow-up to `TASKS_FOR_CODEX.md` Task 7/8  
**Date:** 2026-09-01

This task implements the final agreed responsibility split between the CAM postprocessor, canonical NC release, and the server-side Production Package Creator.

Where this task conflicts with older wording in `TASKS_FOR_CODEX.md`, `AGENTS.md`, examples, commissioning documents, or tests, this task and `docs/postprocessor-production-package-contract.md` are authoritative.

## Product principle — do not change

> **Postprocessor = deterministic NC + stable placeholders.**  
> **Production Package Creator = authoritative placeholder resolution + Machine-specific transformation + immutable Production Package.**

The postprocessor is server-blind. It must not access Meimad Server, database, package context, current Planner assignment, or server master data.

All Meimad identity/context fields in the canonical released NC are placeholders — including **Part Name** and **Operation Name**. Even if CAM/post technically knows those strings, do not use them as authoritative values because typo/staleness/name-drift must never conflict with Meimad server master data.

## 1. Inspect current implementation first

Before editing code, locate and report the existing paths for:

- NC upload/release/versioning and immutable release storage;
- postprocessor/example NC placeholder handling;
- Production Package / package manifest persistence;
- current Machine capability model: CNC vs Manual, Server Verification enabled/disabled, network/delivery capability;
- Tool Table / Tool Offset Table release selection;
- Manual / Dummy Tool Offsets pilot path from Task 8;
- Offset Loader generation and current verification protocol;
- NC verification hook/instrumentation generation;
- current queue action `Create Production Package`;
- package invalidation/supersession and `Ready for Setup` derivation;
- commissioning scripts, Haas examples, simulator scenarios, tests, and documentation that still assume the post writes real Part/Operation names or that every released NC permanently contains active verification code.

Do not create a parallel package builder if an existing one can be extended.

## 2. Define one canonical placeholder grammar

Implement the placeholder contract from `docs/postprocessor-production-package-contract.md`.

Canonical syntax:

```text
[[MEIMAD:<KEY>]]
```

At minimum support the current required keys/concepts:

- `PART_NAME`
- `OPERATION_NAME`
- `PRODUCTION_RUN_ID`
- `PRODUCTION_PACKAGE_ID`
- `MACHINE_ID`
- `NC_RELEASE_ID`
- `OFFSET_LOADER_RELEASE_ID` when applicable
- `EVENT_CONTEXT`
- `VERIFICATION_HOOK`

If existing released NC examples currently use older placeholder spellings, implement a deliberate migration/compatibility policy rather than fuzzy searching. New canonical releases and new postprocessor guidance must use the new grammar.

Do not implement arbitrary free-text replace logic.

## 3. Change postprocessor contract

Update active postprocessor guidance/examples/tests so the postprocessor:

- outputs ordinary deterministic machining code from CAM;
- outputs stable Meimad placeholders at protocol-defined locations;
- does not read Meimad Server;
- does not generate `ProductionPackageId`, current Machine identity, Production Run identity, verification challenge/context, current release identity, creator, or server timestamp;
- does **not** write authoritative `PART_NAME` or `OPERATION_NAME` from CAM/programmer-entered fields;
- places `[[MEIMAD:PART_NAME]]` and `[[MEIMAD:OPERATION_NAME]]` in the required Meimad header/event locations instead;
- emits a deterministic `[[MEIMAD:VERIFICATION_HOOK]]` insertion point rather than permanently forcing active verification logic into every canonical release.

Normal CAM/post data such as geometry, feeds, speeds, tool calls, work offsets, cycles, and control syntax remain normal NC and are not converted to placeholders merely because this architecture exists.

## 4. Canonical NC release remains immutable

Treat the released NC as the exact source/template release produced by CAM/post.

- Never rewrite the canonical release in place when creating a package.
- Keep its immutable release identity/hash.
- Validate placeholder grammar/structure at release time where practical, otherwise before package creation.
- Invalid/malformed required placeholder structure must block Production Package creation.
- Record the generated package-NC hash separately from the source NC release hash.

## 5. Implement deterministic placeholder validation

Create one parser/validator with an explicit schema describing:

- known placeholder keys;
- which are required per post/control/protocol version;
- unique vs repeatable multiplicity;
- allowed locations/contexts where needed;
- optional/conditional keys;
- protocol/version compatibility.

Validation must reject at least:

- missing required placeholders;
- malformed placeholder syntax;
- invalid duplicates for unique keys;
- unknown required keys/version mismatch;
- package output that still contains unresolved required placeholders.

Do not use fuzzy text matching or comments like `contains("PART")` to identify markers.

## 6. Implement the server-side Production Package Creator pipeline

Package creation is initiated by the authorized Tool Room action but executed as a Server-authoritative build.

Required build sequence:

1. Load the exact Operation / Production Run context.
2. Load the concrete Planner-assigned Machine. Do not let the Tool Room/Setup client select another Machine.
3. Load the exact current immutable NC release when NC is applicable.
4. Load the exact Tool Table / Tool Offset Table release/source required by the selected mode, including configured `Manual / Dummy Tool Offsets` support where applicable.
5. Load Machine capabilities and package-generation policy.
6. Parse and validate the canonical NC placeholders.
7. Create a new `ProductionPackageId` and build context.
8. Copy/transform the canonical NC into a package-specific machine-ready NC artifact; do not edit the source release.
9. Resolve all server-owned identity/context placeholders from Meimad authoritative data.
10. Apply Machine-specific verification transformation.
11. Generate applicable Offset Loader artifact.
12. Generate immutable manifest and hashes/checksums.
13. Verify all generated artifacts.
14. Persist all artifacts to server-managed package storage.
15. Atomically activate the package as current only after the complete build validates successfully.
16. Recompute `Ready for Setup` from the existence/validity of this current package.

A failed/partial build must never become current and must never make the Operation `Ready for Setup`.

## 7. Authoritative value sources

Package Creator must use Meimad server/master data for final values.

Examples:

- Part Name -> Case/Part master data;
- Operation Name -> Operation master data;
- Machine ID -> concrete Planner-assigned Machine;
- NC Release ID -> exact current immutable release used for this build;
- Production Run ID -> current server production context;
- Production Package ID -> newly generated server package identity;
- creator -> authenticated authorized user who invoked package creation;
- creation timestamp -> Server timestamp;
- verification/package correlation -> current build/protocol context.

Do not prefer or fall back to CAM-entered Part/Operation names when authoritative Meimad values exist.

## 8. Machine capability rules

### CNC + Server Verification Enabled

Generate at minimum:

1. package-specific runnable NC with all normal identity/context placeholders resolved;
2. finalized Tool Table / Tool Offset Table artifact or explicitly supported configured source mode;
3. unique package-specific Offset Loader bound to the exact Operation + Machine + NC release + package + current verification context.

Expand `[[MEIMAD:VERIFICATION_HOOK]]` into the approved current verification hook/block.

Do not create a second verification protocol. Reuse the existing NC verification and Offset Loader verification/authorization architecture.

### CNC + Server Verification Disabled

Generate at minimum:

1. package-specific runnable NC with identity/context placeholders resolved;
2. finalized Tool Table / Tool Offset Table artifact or explicitly supported source mode.

Remove verification insertion markers cleanly.

The final runnable NC must contain:

- no active Meimad Server-verification hook;
- no unresolved verification placeholder;
- no executable verification Offset Loader generated solely for Server Verification.

### Manual Machine

Do not invent CNC requirements.

- No CNC verification code.
- No executable CNC Offset Loader.
- No NC file unless the actual configured manual process genuinely requires an NC-like artifact.
- Package the applicable manual tool/setup artifacts represented by the current model.

## 9. Manual / Dummy Tool Offsets remains a package input mode

Preserve Task 8's pilot architecture.

`Manual / Dummy Tool Offsets` is an explicit Production Package input/source mode, not a bypass around package creation.

For a verification-enabled CNC Machine using Manual/Dummy Tool Offsets:

- generate the package-specific dummy/manual Offset Loader required by the existing verification/setup-start protocol;
- it carries the exact current package/operation/machine/release/verification identity required by the protocol;
- it contains no measured tool-offset payload;
- Server Verification remains enabled and unchanged.

Do not special-case numeric Machine IDs in domain code. Use Machine configuration/capability.

## 10. Connectivity is delivery capability, not verification policy

Preserve this separation:

- network-connected Machine may offer direct transfer if a supported endpoint/capability exists;
- non-connected Machine must still support normal package open/export/copy workflow;
- temporary disconnection must not silently disable configured Server Verification;
- if configured verification cannot be fulfilled by current Machine/infrastructure configuration, surface a blocking configuration error rather than silently producing a weaker package.

The package builder must work with the existing multi-Communication-Endpoint Machine architecture; do not collapse Machine communication back into one connection type.

## 11. Manifest / audit requirements

The immutable package manifest must retain at least:

- `ProductionPackageId`;
- creator/user identity;
- Server creation timestamp;
- exact Operation / Production Run identity;
- assigned Machine identity;
- exact NC release identity/hash when applicable;
- exact Tool Table / Tool Offset Table release or selected source mode;
- generated Offset Loader release identity/hash when applicable;
- Machine capability snapshot relevant to package generation;
- Server Verification enabled/disabled mode used for the build;
- placeholder/protocol version;
- exact generated runnable NC artifact hash when applicable;
- hashes/checksums for every package artifact;
- supersession/invalidation relationship when it stops being current.

No separate supervisor/approval workflow is introduced. Successful authorized Tool Room package creation is the current preparation completion event; immutable package + manifest are the audit evidence.

## 12. Package invalidation / supersession

Recompute current-package validity when any material input changes, including at minimum:

- assigned Machine changes;
- current NC release changes/is invalidated;
- Tool Table / Tool Offset Table release or configured offset-source mode changes;
- verification-relevant Machine configuration changes;
- a newer Production Package is created for the same Operation/Machine context.

When the current package becomes stale, `Ready for Setup` must no longer remain true merely because an old package still exists on disk.

Historical packages remain immutable audit evidence.

## 13. Role actions / UI behavior to preserve

### NC Creator queue

At minimum:

- Open Case
- Open Operation
- Upload G-code -> existing NC upload/release workflow

NC Creator must not change the Planner-assigned Machine from this workflow.

### Tool Room Manager queue

At minimum:

- Open Case
- Open Operation
- Open Tool Table
- View NC File read-only
- Create Production Package

`Create Production Package` invokes the server builder above.

### Setup queue

Keep the initial context menu intentionally minimal:

- Open Case
- Open Operation
- Open Production Package

Opening/copying/exporting the package is not a workflow transition and must not fake Setup Start.

## 14. Setup start / verification semantics

Preserve current approved semantics:

- current valid Production Package is the readiness boundary for `Ready for Setup`;
- for verification-enabled CNC work, execution of the package's current Offset Loader is authoritative evidence that setup has started according to the existing workflow;
- NC verification and Offset Loader verification/authorization remain separate mechanisms;
- verification timeout change from Task 2 remains authoritative: Offset Loader completion arms verification, while the first main NC start begins the 120-second pending window;
- event sequence numbers remain diagnostic/non-blocking evidence per Task 4.

Do not merge device identity with NC/Offset Loader verification and do not reintroduce Machine Secrets or tablet credentials.

## 15. Documentation cleanup

Update active documentation so no contradictory ownership rule remains.

Inspect/update at minimum where relevant:

- `AGENTS.md`
- `docs/architecture.md`
- `docs/functional-spec.md`
- `docs/data-model.md`
- `docs/api-contract.md`
- `docs/implementation-plan.md`
- current postprocessor docs/examples
- commissioning/audit docs
- Haas example NC files/macros
- simulator scenarios
- tests

The new authoritative postprocessor contract is documented in:

`docs/postprocessor-production-package-contract.md`

Do not delete historical/versioned `.docx` documents solely because they contain old wording; update the active/canonical guidance and clearly supersede contradictory active rules.

## 16. Required acceptance tests

Add automated tests proving at least:

- postprocessor/canonical fixture contains `PART_NAME` and `OPERATION_NAME` placeholders rather than programmer/CAM values;
- postprocessor fixture requires no Server access;
- same source fixture yields deterministic placeholder layout;
- missing/malformed/invalidly duplicated required placeholders block package creation;
- final Part Name and Operation Name come from current Meimad server master data even when CAM fixture contains different/typo text elsewhere;
- source NC release remains byte/hash-identical after package generation;
- generated runnable NC gets its own hash/identity;
- verification-enabled CNC package resolves all placeholders, injects approved verification hooks, and generates a unique bound Offset Loader;
- verification-disabled CNC package resolves identity/context placeholders, removes verification markers, and has no active Server-verification code or verification Offset Loader;
- Manual Machine package contains no invented CNC verification executable;
- Manual/Dummy Tool Offsets package follows the same package lifecycle and, when verification-enabled, gets an identity-bound dummy Offset Loader with no measured offset payload;
- package build is atomic: any artifact/validation failure leaves no current half-built package and does not produce `Ready for Setup`;
- Package Creator cannot build for a Machine different from the Planner assignment supplied by server authority;
- Machine reassignment invalidates the old current package;
- NC release change invalidates the old current package;
- Tool/Offset source or release change invalidates the old current package;
- verification configuration change invalidates the package when generated content is affected;
- disconnected Machine does not silently downgrade verification policy;
- package opening/copying/exporting does not change workflow state;
- one generated Offset Loader cannot become current for another Operation/package;
- server manifest retains exact actor/time/releases/modes/hashes required to reconstruct the build.

## 17. Completion report

When finished, report:

- files changed;
- old responsibility assumptions found and removed;
- final placeholder grammar/schema and validation rules;
- authoritative source for each resolved field;
- exact Package Creator transformation pipeline;
- package composition by Machine type/verification mode;
- Manual/Dummy Tool Offsets behavior;
- package storage layout and manifest schema;
- invalidation/supersession rules;
- how `Ready for Setup` is derived;
- verification/Offset Loader behavior preserved;
- migrations/backward-compatibility treatment of older placeholder formats/releases;
- tests executed and results;
- any remaining real-machine commissioning step.
