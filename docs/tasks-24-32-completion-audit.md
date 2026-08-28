# Tasks 24-32 completion audit

Audit date: 2026-08-28

This matrix maps every requirement in Codex Tasks 24 through 32 to repository evidence. It separates software completion from physical Machine acceptance.

Status meanings:

- `IMPLEMENTED_AND_TESTED`: repository implementation and automated evidence exist.
- `IMPLEMENTED_WITH_EXPLICIT_BOUNDARY`: the supported behavior exists, but the Server deliberately does not invent evidence that the available protocol cannot prove.
- `PHYSICAL_NOT_READY`: repository tools exist, but real-Machine acceptance has failed or remains incomplete.

Overall result: Tasks 24-30 and 32 are implemented and covered by repository tests. Task 31 is `PHYSICAL_NOT_READY`; therefore protected Haas verification is not production-ready and remains disabled.

## Task 24 - Add Explicit Anomaly Tracking

Status: `IMPLEMENTED_WITH_EXPLICIT_BOUNDARY`.

The immutable schema-v59 ledger, bounded queue API, stable catalog, idempotent creation, and deterministic human messages are implemented in:

- `server/Meimad.Planner.Server/Application/Anomalies/OperationalAnomalyService.cs`
- `server/Meimad.Planner.Server/Persistence/SchemaV59OperationalAnomaliesMigration.cs`
- `server/Meimad.Planner.Server/Persistence/SqliteOperationalAnomalyRepository.cs`
- `server/Meimad.Planner.Server/Api/Anomalies/OperationalAnomalyEndpoints.cs`

The catalog contains all required types:

`wrong_nc_program`, `active_nc_identity_unavailable`, `stale_offset_loader`, `offset_loader_not_executed`, `offset_loader_interrupted`, `verification_failed`, `verification_expired`, `verification_macro_version_mismatch`, `cycle_started_before_qc_pass`, `cycle_end_without_start`, `cycle_interrupted`, `cnc_event_sequence_gap`, `duplicate_cnc_event`, `unknown_production_run`, `ambiguous_production_run`, `tablet_offline`, and `tablet_credential_revoked`.

Automated evidence is in `tests/Meimad.Planner.Server.Tests/Anomalies/OperationalAnomalyTests.cs` and `tests/Meimad.Planner.Server.Tests/Cnc/CncVerificationFoundationTests.cs`. The ingestion regressions prove that missing or mismatched NC identity cannot create an Offset Loader verification session, resolve a protected-macro result, or advance a production cycle. Verification results must also repeat the exact current Offset Loader release token and nonce, so delayed evidence from an older challenge cannot resolve a newer session. The tests also prove that a source identifier reused for a conflicting cycle event is rejected and recorded. Anomaly recording appends operational evidence; it contains no planning-assignment, quantity, allocation, or backlog mutation path.

Boundary: automatic `offset_loader_interrupted` and `tablet_offline` detection needs an approved authoritative interruption signal or offline threshold. The types are supported, but the Server does not fabricate silence-only facts.

## Task 25 - Add Safe Recovery Actions

Status: `IMPLEMENTED_AND_TESTED`.

Required recovery paths and evidence:

| Requirement | Repository evidence |
|---|---|
| Invalidate current verification session | `CncVerificationFoundationService.cs`, `CncVerificationEndpoints.cs`, and `CncVerificationFoundationTests.Authorized_recovery_invalidates_session_and_revokes_current_offset_loader_with_audit` |
| Revoke current Offset Loader release | Same service/endpoint/test; immutable release history is retained |
| Generate a new Offset Loader release | `CreateOffsetLoaderReleaseAsync`, the Setup recovery UI, and `CncVerificationApiTests.Windows_recovery_routes_require_edit_authority_and_preserve_release_history` |
| Reassign replacement tablet | `EInkDeviceRegistrationEndpoints.cs`, `UserTerminalsViewModel.cs`, and `UserTerminalsViewModelTests.Edit_mode_enables_assignment_revoke_rotation_and_spare_actions` |
| Rotate tablet credential | Same registration and User Terminals surfaces; `EInkApiTests.Active_editor_can_register_bind_revoke_and_rotate_a_device_token` |
| Retry QC workflow after failure | `QcWorkflowService.cs` and `EInkApiTests.Qc_queue_supports_fail_resend_and_pass_with_user_reason_and_approval_time` |

All actions require the appropriate editor/credential scope and write attributed audit or immutable workflow evidence. The Windows Setup view explicitly says recovery restores a valid process. There is no generic `BYPASS VERIFICATION` button, service method, or route.

## Task 26 - Track Protected CNC Macro Version Per Machine

Status: `IMPLEMENTED_AND_TESTED`.

`SchemaV50CncVerificationFoundationMigration.cs` stores `expected_macro_version` per Machine. Strict DPRNT parsing requires `MACROVERSION`; `CncDprintEventIngestionService.cs` blocks a mismatch, records `verification_macro_version_mismatch`, and surfaces the exact message `CNC VERIFICATION MACRO UPDATE REQUIRED`. Schema-v61 application validation also refuses to enable quarantined macro versions 1-5 while allowing disabled forensic configuration to remain visible.

Evidence: `CncVerificationFoundationTests.Protected_macro_result_resolves_session_idempotently_and_enforces_version`, API tests, strict simulator transcripts, and the physical checklist's still-open commissioning gate.

## Task 27 - Add Machine-Level Verification Configuration

Status: `IMPLEMENTED_AND_TESTED` for repository behavior; physical values remain commissioning data.

Schemas v50, v60, and v61 plus `CncVerificationFoundationService.cs` provide Machine-scoped DPRINT transport/port, three distinct protected program numbers, optional custom G-code alias, four persistent handshake variables, one persistent event-sequence variable, Machine verification secret, expected macro version, response-code digits, timeout, and enablement/version control. Validation restricts M109 storage, canonicalizes Haas legacy aliases, rejects collisions in both application and database layers, and leaves upgraded null mappings unguessed.

The secret is protected with ASP.NET Data Protection and public DTOs return only `SecretConfigured`. `CncVerificationFoundationTests.Verification_secret_is_encrypted_preserved_on_update_and_never_returned` proves encryption, preservation, and non-return. E-Ink API tests prove ordinary tablet projections contain neither the Machine secret nor nonce/variable mapping.

## Task 28 - Build a CNC Event Simulator

Status: `IMPLEMENTED_AND_TESTED` as development tooling only.

`tools/Meimad.Planner.CncSimulator` is a loopback-by-default TCP/ASCII Haas DPRNT peer. Its JSON scenarios cover Offset Loader completion, incorrect/correct verification, stale loader, QC/tablet event encodings, cycle start/end, interruption, duplicate delivery, missing/out-of-order sequences, delay, and next Production Run. A scenario selects Machine, Production Run, NC identity, Offset Loader release/token, sequence, and relative timing (`atMs`/`delayMs`). DPRNT v1 has no Machine timestamp, so the Server truthfully records receipt time instead of inventing one.

The simulator adds no Server endpoint and cannot mutate production unless an administrator deliberately points a development Machine connection at it. `scripts/test-cnc-machine-output-simulator.ps1` verifies strict ASCII/CRLF output, duplicates, and overwrite refusal. `CncPlatformTests` covers the Server-side bench consumer.

## Task 29 - Extend the Existing E-Ink Simulator

Status: `IMPLEMENTED_AND_TESTED` for browser simulation; physical panel acceptance is separate.

`server/Meimad.Planner.Server/EInkSimulator` displays all seven fixed states: `READY_FOR_SETUP`, `IN_SETUP`, `IN_SETUP_RUN`, `IN_QC`, `READY_FOR_PRODUCTION`, `IN_PRODUCTION`, and `BLOCKED`. It simulates a verification code, failure, expiry, Server-offline last-known-good content, low battery, revision change, and official `SEND_TO_QC`.

Local scenario controls never change Server data. The separate official button posts only `{ event_type: "SEND_TO_QC" }` and is enabled only for the authoritative `IN_SETUP_RUN` state. `EInkApiTests.Simulator_covers_workflow_failures_and_only_posts_exact_send_to_qc` checks these markers and the contract shared with tablet status behavior.

## Task 30 - End-to-End Development Scenario

Status: `IMPLEMENTED_AND_TESTED`.

`CncVerificationFoundationTests.Full_development_workflow_reaches_production_and_closes_on_next_setup` uses real application services and schema, not an unauthenticated mutation shortcut. It covers the required 29-step scenario:

| Steps | Evidence in the integrated test |
|---|---|
| 1-4 | Seed Production Runs, package and tool readiness, assert `READY_FOR_SETUP`, configure the Machine, and create the current Offset Loader release |
| 5-11 | Ingest OLC with nonce/release evidence, validate it, assert `IN_SETUP`, calculate a six-digit response, and expose it through tablet status |
| 12-14 | Ingest SVF, create a fresh challenge, ingest SVS, and assert `IN_SETUP_RUN` |
| 15-20 | Submit authenticated `SEND_TO_QC`, assert `IN_QC`, record QC FAIL, resend, record QC PASS, and assert `READY_FOR_PRODUCTION` |
| 21-23 | Ingest valid CST/CEN, assert `IN_PRODUCTION`, and verify counted output |
| 24-26 | Exercise START/START interruption, duplicate delivery, and a sequence gap/end-without-start anomaly |
| 27-29 | Assign/start the next Run setup, close the prior production session retroactively, and verify the readable debug timeline plus anomaly/audit rows |

## Task 31 - Physical Machine Commissioning Checklist

Status: `PHYSICAL_NOT_READY`.

The authoritative record is `docs/cnc-commissioning-checklist.md`, supported by `scripts/audit-cnc-commissioning-checklist.ps1`, `scripts/test-cnc-commissioning-checklist.ps1`, `docs/cnc-verification-code-audit-2026-08-27.md`, and `docs/haas-internal-engineering-review-2026-08-27.md`. External HFO approval is not required.

Current fail-closed audit result:

- 4 `PASS`
- 1 `FAIL`
- 9 `NOT_TESTED`
- 5 incomplete Machine/controller identity fields
- both commissioning sign-offs missing

The blocking physical evidence is that the VF-3SS accepted a correct response after at least 130 seconds at M109 and its `#3001`-derived sequence is not monotonic across reboot/wrap. The full code audit also found that v3-v5 result records lack the release-token/nonce correlation required to prevent a delayed result from binding to a newer challenge. The Server rejects that old format, and v3-v5 plus packages v1-v3 remain quarantined. Schema v60 and `scripts/new-haas-verification-v6-bench-pack.ps1` now provide a separately numbered no-motion candidate with a distinct protected finalizer, exact result correlation, and a configured persistent sequence that fails closed instead of wrapping. `PERSISTENT_COUNTER` is the selected product design, with one-time positive initialization at 1. `scripts/new-haas-ngc-engineering-test-pack.ps1` and `docs/haas-ngc-engineering-machine-tests.md` provide no-motion real-controller probes for the six open engineering questions, but no physical result has been claimed. The v6 structural test passes, but the candidate is not internally signed, controller-loaded, or physically approved. `docs/haas-bounded-retest-after-internal-approval.md` is explicitly `DO NOT RUN` until the written internal engineering/design gates are satisfied. Unit and simulator tests do not make this production-ready.

## Task 32 - Documentation Cleanup

Status: `IMPLEMENTED_AND_TESTED`.

Every minimum-listed document was inspected and now carries the accepted boundary:

- `AGENTS.md`
- `docs/functional-spec.md`
- `docs/architecture.md`
- `docs/data-model.md`
- `docs/api-contract.md`
- `docs/implementation-plan.md`
- `docs/production-run-architecture.md`
- `docs/esp32-eink-work-tablet.md`
- `docs/haas-active-program-header.md`
- `firmware/esp32-eink-mvp/README.md`

The required decisions are explicit:

`Persistent CNC workflow mode variable: REMOVED`

`Protected temporary setup verification variables: SUPPORTED`

The supported variables are temporary, configured, protected handshake data only. They are never persistent Setup/Production state or Server workflow authority.

## Regression evidence

The aggregate verification record is `docs/verification-report.md`. Its current separately executed Debug and Release totals are 619 Server tests plus 244 Windows Client tests: 863 passed per configuration, with zero failures and zero skips. Simulator validation, all three ESP32 environments, installer payload verification, and focused commissioning-script checks are also recorded there. This evidence does not replace Task 31 physical acceptance.
