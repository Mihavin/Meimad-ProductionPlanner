# Meimad implementation tasks — audit repairs, industrial UI and local AI

Repository status: see [the implementation record](../implementation-plan.md#audit-handoff-2026-10-08). C01/C02/C03/C04/C06 have implementations and executable validation; remaining task descriptions below are retained requirements, not completion claims.


Prepared 8 October 2026 from the GPT-6 Astra audit of `Mihavin/Meimad-ProductionPlanner`, `main` at `b13f841886e910c5debcc8f635a207912f833547`. This is an implementation handoff, not a statement that the work is complete. Source paths below are relative to that repository. Recheck the current branch before coding; do not reimplement a finding already fixed after the audited revision.

Use this as a new task set, preserving unrelated instructions in `TASKS_FOR_CODEX.md`. Suggested repository destination: `docs/tasks/audit-and-local-ai.md`, with a short link from the working task buffer. The companion audit explains findings F1–F11; the interactive workbench concept supplies visual direction and fictional data, not production logic. No repository files or production services were changed while preparing this handoff.

## 1. Execution instructions and target outcome

Implement in small, reviewable changes. Begin with P00, then correctness and recovery tasks. Build the local AI evidence layer after the relevant domain contracts are stable. A task is complete only when its behavior, migration, API/client compatibility, tests and documentation have been addressed. Report blocked product decisions individually and continue independent tasks.

The intended result is a reliable factory-local planner with coherent Run/package identity, predictable concurrent editing, durable evidence, complete recovery, a clear industrial workbench, and optional local AI that explains operational facts with inspectable evidence.

Preserve these boundaries throughout:

- The Planner server remains the only authority for planning and the only process opening the production SQLite database. Keep the database on a local disk.
- Machine assignment and backlog order remain manual. Preserve deterministic auxiliary resource prediction and all four dependency modes, including current locked-simultaneous flow-line semantics.
- Preserve Cases, Orders, Batch Operations, Manufacturing Programs and Production Runs as distinct concepts. Coupled program outputs remain atomic; never round quantities or allow overproduction to resolve an allocation problem.
- Original engineering files and released canonical NC templates remain unchanged. Generated package files remain immutable and carry their own manifest/hashes.
- Retain existing Windows sign-in and permissions. Do not add tablet credentials, CNC secrets, a second identity system, or mandatory package supervisor approval.
- Preserve NC/Offset Loader verification: exact binding; `ARMED` has no timeout; first intended main NC start begins `PENDING`; successful binding is reused; a newly executed current loader supersedes the previous binding. Sequence gaps alone are evidence, not authority.
- TV remains read-only. E-Ink keeps its existing server-resolved scope, last-known-good cache, and narrow `SEND_TO_QC` command. The UI redesign targets WPF; the HTML concept is not a request to replace WPF with a website.
- Keep outbound ERP/portal features under their existing explicit configuration. Hardening an implemented path does not authorize enabling it on a production installation.
- Local AI has no planning, ERP, package-release, offset or CNC mutation capability. Its outage must not interrupt the planner.

### Common definition of done

Every implementation change must include: problem and task ID; inspected baseline; changed code paths; migration/compatibility notes; relevant executable tests and actual results; updated specifications/contracts; rollout and recovery instructions; unresolved physical or deployment checks. Never substitute “tests exist” for “tests ran and passed.” Use the next available ordered migration number at implementation time; do not reserve v91 from this snapshot.

## 2. Backlog and dependencies

Size is relative scope, not a calendar estimate: S = narrow change, M = cross-layer feature, L = migration or several coordinated layers. Split L tasks into reviewable changes without splitting an atomic correctness fix.

| ID | Deliverable | Priority / size | Depends on |
|---|---|---|---|
| P00 | Baseline, decision register and compatibility inventory | First / M | — |
| C01 | Durable manual timing and setup-session reconstruction | P1 / L | P00 |
| C02 | Versioned and semantically valid auxiliary pins | P1 / M | P00 |
| C03 | Exact Run/program/output package and queue context | P1 / L | P00 |
| C04 | Atomic current-package publication and idempotency | P1 / L | C03 |
| C05 | Unified readiness and transition contracts | P1 / L | C03, decision D2 |
| C06 | Cycle-idempotency payload validation | P2 / S | P00 |
| C07 | Kitaron optimistic concurrency | P1 when push used / M | P00, decision D1 |
| C08 | Durable ERP push intent and uncertain-result reconciliation | P1 when push used / L | C07 |
| D01 | Complete backup sets and replacement-PC restore | P1 operational / L | P00 |
| D02 | Coherent local projection revision and snapshots | P2 / L | C01, C02, C03 |
| D03 | ERP staging, mapping provenance and safe preview/apply | P2 / L | P00, decision D1 |
| D04 | Artifact publication reconciliation and retention | P2 / M | C04, D01 |
| D05 | Performance baseline, batched queries and bounded projections | P2 / L | D02 |
| D06 | Operational health and diagnostic evidence | P2 / M | D01, D02 |
| U01 | Shared WPF navigation, context and industrial themes | P2 / M | P00 |
| U02 | Preparation queues and approved handoff semantics | P2 / L | C03, C05, U01, decision D3 |
| U03 | Operation and package workbench | P2 / L | C04, C05, U01 |
| U04 | Shop overview and integration workspace | P2 / L | D02, D03, D06, U01 |
| U05 | Keyboard, DPI, multilingual and accessibility acceptance | P2 / M | U02, U03, U04 |
| A01 | Typed evidence contracts and deterministic explanation service | AI foundation / M | C03, C05, D02, D06 |
| A02 | Optional local runtime adapter and bounded request lifecycle | AI foundation / M | A01 |
| A03 | Schema, citation and claim validation | AI foundation / M | A01, A02 |
| A04 | Local incident/document retrieval | AI pilot / M | A01, A03, decision D6 |
| A05 | Blocker explanation and shift-summary use cases | AI pilot / M | A03; A04 for historical search |
| A06 | WPF AI panel with evidence and cancellation | AI pilot / M | A05, U03 |
| A07 | Model selection, multilingual evaluation and deployment package | AI release gate / L | A05, A06 |
| A08 | Pilot monitoring, feedback and controlled expansion | AI rollout / M | A07, R01 |
| R01 | Integrated release rehearsal and operational acceptance | Release / L | Released feature slice and its prerequisites |
| R02 | Documentation and implementation completion report | Each release / S | Completed tasks in that release |

Recommended release sequence: **1** correctness plus backup; **2** coherent data and performance; **3** workbench; **4** optional local AI pilot. AI contract and mock-runtime work may proceed against stable interfaces before the UI redesign is complete. Do not delay urgent repairs to deliver every screen or AI feature together.

### Decisions to record in P00

These are the remaining consequential choices. This backlog defines the proposed implementation scope; it does not resolve contradictory domain semantics by itself. Preserve deployed behavior until the relevant decision is recorded. Do not block unrelated repairs.

| Decision | Conflict or missing information | Suggested resolution | Blocks |
|---|---|---|---|
| D1 ERP ownership | Code implements outbound SQL push; historical intent was read-only import | Keep installed enablement unchanged. Document allowed outbound fields, owner and overwrite policy. Import-only installations remain import-only | Enabling/expanding push; field-ownership policy in C07/D03 |
| D2 Stage gates | Resolved by explicit owner approval, 2026-10-08 | Approved stage matrix; unknown capacity blocks managed work requiring tools, zero-tool work remains allowed; unmanaged legacy NC/tool-release exemptions retained with material readiness and valid execution context | C05 complete locally |
| D3 Physical handoff | `TASKS_FOR_CODEX.md` §3 requires handoff; §7 makes package creation sufficient for Ready for Setup | Prefer separate “Package ready” and “Handed to setup” facts. Decide whether handoff blocks production/setup or is tracking only; no invented handoff for old work | U02 state transition policy |
| D4 Non-loader setup | Machines without an executable loader need a real setup-start source | Retain an existing valid signal; otherwise specify the permitted actor/event instead of generating a fake loader | New setup-start behavior on affected machines |
| D5 Recovery/retention | Production evidence retention and acceptable loss/downtime are not measured | Pilot target: 15–60 minute DB recovery point and same-shift restore; agree actual retention and backup destination | Operational acceptance of D01/D04 |
| D6 AI data/hardware | Actual server/GPU capacity and approved document corpus are unknown | Start with structured server facts and local logs, one inference request, multilingual 7–14B-class candidate evaluation; add selected documents later | Model selection and production AI enablement |

## 3. Correctness tasks

### P00 — establish baseline and decision register

Read the current `AGENTS.md`, functional/API/data-model specifications, production-run architecture and complete existing task buffer. Compare current HEAD with the audited commit. For every F1–F11 finding, mark present, already repaired, changed or unconfirmed, with current source links and a reproducer where possible. Inspect existing tests before adding new ones.

Create/update `docs/implementation-plan.md` with D1–D6, accepted decisions already present in current code, and affected tasks. Explicitly reconcile package-create permissions: the audited `AccountModels.cs` allows every signed-in account to generate a package; older task prose names Tool Room. Preserve the documented current permission unless deliberately changed.

Inventory schema, Windows/TV/E-Ink API versions, package manifests and installed service configuration. Define client upgrade behavior for new required version/context fields. Old clients may retain safe reads; unsafe unversioned mutations must receive a precise upgrade/precondition response, not synthesized “latest” versions. Capture existing conflict payload conventions and reuse them.

Acceptance: a developer can identify the base commit, decision status, migration baseline and first independent repair without guessing. Do not enable ERP push, change production configuration or run test mutations against factory hardware during baseline work.

### C01 — replace globally capped timing reconstruction

**Audit:** F1. **Start at:** `Application/Timeline/TimelineProjectionService.cs`, `Persistence/SqliteStructuredEventLogRepository.cs`, manual-report command handlers and existing timing tests under the server project.

Replace the latest-5,000-global-events dependency with a complete, operation-scoped timing source. Retain original immutable event payloads, but normalize frequently queried event/operation/Run/Machine references. Define an explicit setup-session association; never pair an arbitrary latest start with an unrelated latest end.

Specify current-value semantics before migration: distinguish per-part duration from batch duration, quantity weighting, duplicate reports, corrections, zero/negative duration, restart, open setup session and unknown data. Preserve the chosen current-working-time behavior; do not create an unrequested plan-versus-actual analytics product.

Backfill from all retained relevant history in deterministic pages. A malformed/missing/ambiguous event produces a recorded migration/projection warning and unknown/fallback behavior; do not manufacture evidence. Build a rebuildable aggregate with source checkpoint, calculation version and sample count. Make new command/event/aggregate updates atomic or use a durable incremental projector whose lag is visible and whose reads cannot silently claim completeness. Choose and document one approach.

Acceptance tests: original timing unchanged after >5,000 unrelated reports; two setup sessions paired correctly; duplicate/retracted/corrected samples; concurrent reports; restart during backfill; old DB migration; missing historical evidence. Shadow-compare old/new values before switching the timeline. Do not “fix” this by increasing the global limit.

### C02 — version and validate auxiliary resource pins

**Audit:** F2/F3. **Start at:** `Api/Timeline/TimelineEndpoints.cs`, `Application/Timeline/ITimelineAuxiliaryPinRepository.cs`, `Persistence/SqliteTimelineAuxiliaryPinRepository.cs` and resource-planning tests.

Require the expected pin version or a stable parent assignment stamp for create/update/clear. Preserve a monotonic stamp across deletion/recreation, or use a parent aggregate version/tombstone so an old “no pin” view cannot pass after an intervening create/delete. Compare it in the same transaction as the write. Return the existing conflict structure with current actor/time and state. Do not delete/reinsert at version 1.

Within that transaction, prove the resource requirement belongs to the Batch Operation's actual Case Operation/route context. Validate resource class, active status and applicable skill/capability requirements. Separate ordinary calendar contention, which shifts prediction, from invalid configuration. If an authorized capability exception exists, record it explicitly rather than allowing unrelated IDs.

Acceptance: stale update and stale clear fail without changing data; simultaneous creates have one winner; create/delete/recreate cannot bypass a stale stamp; real but unrelated requirement rejected; wrong resource class rejected; valid manual pin respected by deterministic recalculation. Client preserves its draft on conflict.

### C03 — bind packages and queue entries to exact Run context

**Audit:** F5. **Start at:** `Persistence/SqliteProductionPackageRepository.cs`, `Persistence/SqlitePreparationQueueRepository.cs`, package/preparation DTOs and the corresponding Windows view models.

Remove first-output-by-sort-order Run selection. Resolve through the active assignment and validate Run/program/output membership against the quantity obligation. Explicitly define the actionable row identity for a multi-output/multi-Run operation. APIs must carry enough IDs to select one concrete context; those IDs never authorize changing the planner-assigned Machine.

Return an explicit ambiguity or stale-context result when an old client cannot identify a unique Run. Preserve historical packages and cancelled Runs for inspection without accidentally making them current. Ensure package downloads, queue actions, event links and setup restart all use the same resolved identity.

Acceptance: one Batch Operation split across two live Runs/Machines; cancelled historical Run ordered before current; multiple programs in a Run; coupled outputs; assignment changes during request; no active assignment. Each queue row, package and loader must bind to the intended live context. No arbitrary `LIMIT 1` fallback.

### C04 — make package generation retry-safe and publication atomic

**Audit:** F4. **Start at:** `Application/ProductionPackages/ProductionPackageService.cs`, `Persistence/SqliteProductionPackageRepository.cs`, `Api/ProductionPackages/ProductionPackageEndpoints.cs`, `tests/.../ProductionPackages/`.

Extend build context with exact Run/program/output identity, relevant input/configuration versions and observed current-package pointer/version. Stage and hash files outside the database write transaction. Inside the short activation transaction, compare every binding that affects generated content and compare the observed current-package stamp. Activate package/loader pointers and record actual predecessor atomically.

Add request idempotency scoped to the command and actor/context as appropriate. Canonicalize/hash relevant request fields. Same key/same request returns the original package/result; same key/different request conflicts. Two distinct builds from one predecessor cannot both silently succeed as current. Reuse completed results on lost-response retries; deliberate regeneration uses a new request ID and a visible revision.

Handle failed/abandoned staging without deleting referenced immutable artifacts. D04 supplies restart reconciliation. Keep current package generation permissions and avoid an extra sign-off role.

Acceptance: simultaneous P0→P1/P2 builds; retry after commit but before response; input/assignment/measurement changes mid-build; supersession chain points at the actual predecessor; corrupt staged file; disk-full failure; failed activation leaves old package current; original source hashes unchanged. Verify loader supersession and existing CNC-binding semantics separately.

### C05 — unify readiness and transition policy

**2026-10-08 status: complete locally; D2 approved.** Shared preparation decisions, exact transactional Run readiness, Start evidence comparison, Resume/cycle/manual-production revalidation and Windows rendering are implemented locally. The owner explicitly approved applying the operation capacity rule consistently to Runs: unknown capacity blocks managed work requiring tools; zero-tool work remains allowed. Unmanaged legacy NC/tool-release exemptions remain, with material readiness and valid execution context required. Setup/operation actions, client Start stamps and CNC start checks are also integrated. Both D2 mode policies were explicitly approved on 2026-10-08. See the implementation plan for verification and scope; deployment, U02 UI work and controller commissioning remain separate.

**Audit:** F6 and UI semantics. **Start at:** `Application/ProductionRuns/ProductionRunReadinessService.cs`, `ProductionRunExecutionService.cs`, operation readiness evaluator, `Persistence/SqliteMachineAssignmentRepository.cs`, preparation projector.

The D2-approved implementation uses a single server-owned policy evaluated for specific actions. Return structured reason codes, required evidence, current evidence, blocking/attention classification and context stamp. The Windows client renders these results; it must not recreate gates locally.

| Action | Contract to implement |
|---|---|
| Plan/assign | Valid domain allocation and Machine compatibility; incomplete preparation shown separately; preserve permitted forward planning |
| Create package | Exact current engineering releases, Machine mode, required tool/offset data and valid Run context |
| Mark physical handoff | Only if D3 enables it; current package plus permitted actor/operator qualification; preserve factual material state |
| Record setup start | Actual approved Machine/loader evidence or D4-approved alternative; exporting files is never setup start |
| Start/record production | Approved material and engineering gates plus valid execution context; commissioned controller verification remains a separate required boundary where configured |

Explicitly cover measured offsets, manual/dummy offsets, verification-disabled CNC, unmanaged legacy and manual machines. Unknown tool capacity must have a named policy, not accidentally pass because it is null. Do not require measured offsets from a Machine explicitly configured for manual/dummy mode; preserve its required verification loader/hooks.

Validate the full relevant context at the mutation commit, not only the Run version read earlier. Reuse typed facts/context stamps to avoid a time-of-check/time-of-use gap. Do not conflate `Run Start`, setup start, first-piece QC and physical production.

Acceptance: identical prerequisites yield consistent action-specific decisions across operation/Run APIs; changed readiness between read and commit rejected; stale/wrong-post NC, missing tool release, partial material and unknown capacity handled deliberately; every supported manual/legacy exception tested; reset/resume and existing exact CNC binding preserved. A passing API test is not proof of controller commissioning.

### C06 — validate duplicate cycle-event binding

**Audit:** F7. **Start at:** `Persistence/SqliteProductionRunExecutionRepository.cs` and Run cycle API tests.

Keep intended deduplication key scope, but store/compare the immutable Run/program and event payload fields for that key. Identical retry returns the original receipt and original target result. Same key with different Run/program/count or other meaningful fields returns a binding conflict. Decide whether any metadata may legitimately differ on transport retry and exclude only those fields from canonical payload comparison.

Acceptance: same event retry counts once; cross-Run/program reuse conflicts; duplicate request naming nonexistent target does not succeed using another Run's receipt; counts and coupled-output allocations remain exact; parallel duplicates have one durable observation.

### C07 — protect ERP rows against concurrent changes

**2026-10-08 status: implemented locally; isolated SQL Server acceptance pending.** Typed comparison values, locked transactional revalidation, whole-push rollback, trigger-independent direct row counts and Windows preview stamps are implemented. D1 fields and enablement are unchanged. See the implementation plan for test results and the opt-in disposable LocalDB tests; no factory ERP acceptance is claimed.

**Audit:** F8. **Start at:** `Application/Kitaron/Push/SqlServerKitaronPushTarget.cs`, `KitaronPushPlanner.cs`, push service and tests.

For approved outbound fields, read an ERP-supported row version or capture comparison values with defined null/decimal/date semantics. Revalidate them and open/stopped status in the same SQL transaction as updates. Use an appropriate consistent read/isolation strategy supported by the actual Kitaron database; do not enable SQL Server options blindly. Avoid `NOLOCK` for correctness-sensitive comparison reads.

Affected-row counts must prove the expected rows changed. Any failed predicate rolls back the intended atomic push unit and yields a conflict with fresh preview. Document trigger behavior and whether the ERP exposes an approved integration interface. Retain separate read-only credentials/role for installations that only import; do not treat `ApplicationIntent.ReadOnly` as write protection.

Acceptance on an isolated representative SQL Server: modify a field, close/stop an order, delete a target or trigger a validation failure between preview/write. No intervening change is overwritten. Different independent push items follow the explicitly defined atomic-unit policy. Never use the real factory ERP for these tests.

### C08 — record durable ERP intent and reconcile unknown outcomes

**2026-10-08 status: implemented locally; isolated SQL Server acceptance remains pending.** Schema v101 retains immutable intent and lifecycle, claims once before writing, recovers interrupted work without replay, distinguishes proven rollback from uncertain commit/receipt outcomes, and provides versioned reconciliation plus explicit acknowledgment in Windows. Matching ERP values remain non-attributable evidence. See the implementation plan for verification and deployment limits.

**Audit:** F9. **Start at:** `KitaronPushService.cs`, `Persistence/SqliteKitaronPushRepository.cs`, push result DTOs and `KitaronPushViewModel.cs`.

Persist the intended field changes, source/target context, correlation ID and comparison values before external write. Use explicit lifecycle states such as `Prepared`, `Writing`, `Succeeded`, `FailedBeforeCommit`, `OutcomeUnknown`, `Reconciled`. Record cancellation and process restart consistently. A local receipt failure or connection loss around ERP commit must never display “nothing was written” without evidence.

Use a small durable outbox for approved asynchronous deliveries where restart survival is needed. At-least-once delivery does not provide a distributed transaction. Retry only when idempotency/comparison makes it safe. Reconciliation reads approved ERP values, records what can be established, and leaves ambiguous outcomes visible for resolution; equal values alone may not prove who wrote them.

Acceptance: failures before write, during transaction, immediately after external commit and before local receipt; service restart in every state; cancelled request; concurrent reconciliation; duplicate worker claim. UI distinguishes proven failure from unknown outcome and offers inspect/reconcile without blind overwrite.

## 4. Storage, data flow and performance

### D01 — make a complete, independently recoverable backup set

**Audit:** F10. **Start at:** `Backup/SqliteBackupService.cs`, backup configuration/API, release/package options, Data Protection registration and existing backup tests.

Preserve online SQLite snapshot, integrity/foreign-key checks, checksum and test restore. Extend recovery from a DB file to a manifest-backed set: DB/schema/app version; all referenced immutable release/package artifacts and hashes; configuration; recoverable Data Protection key material/service-identity instructions; separately owned engineering-folder dependencies. Keep secret material protected and out of logs/git.

Take the DB snapshot first; enumerate its artifact references and prevent their cleanup while creating the set. Copy/verify those immutable files, then publish the backup-set validity marker. An incomplete set must not be advertised as restorable. Use an independent destination; a second directory on the same disk is insufficient for disk failure.

Choose one scheduled-backup owner (existing external scheduler or server job) and avoid duplicate schedules. Show last complete backup age/result. Implement retention only after D5 is decided. Restore into a separate location; stop/quiesce the service during activation, validate version/configuration and reopen representative artifacts before returning to operation. Do not imply old binaries can read a newer schema.

Acceptance: missing/corrupt referenced file, disk full, interrupted copy, permissions loss, backup during package publication, damaged DB, wrong key/service identity; restore on a replacement Windows PC and open current/historical packages. Record measured loss window and recovery time. Real replacement-PC acceptance remains required even if unit tests pass.

### D02 — establish a coherent projection snapshot and revision

**Audit:** F11. **Start at:** `Application/PlanningBoard/PlanningBoardService.cs`, timeline source reader/projector, readiness and Run projection services.

Compute related local board/timeline/readiness facts from one read snapshot or a version-stable bounded retry. Define a logical plan revision that changes transactionally for every input affecting prediction: assignments/backlog, resource/calendar changes, relevant timing, allocations, route and preparation facts. Use projection-specific subversions if a single global counter invalidates too much; correctness comes before cache hit rate.

Return revision, computed/as-of timestamps and freshness metadata. Time itself can change projections without writes: include the evaluation instant/bounded time policy in cache identity and invalidation. Reuse source snapshots across board/conflict computations instead of rereading unrelated moments. Release read transactions before slow CPU work or network/model calls.

Acceptance: concurrent assignment/calendar/event updates cannot produce a mixed-version “current” board; continuously changing state ends in a bounded retry/stale result rather than an infinite loop; time-window changes invalidate appropriate results; cache miss/hit gives equivalent deterministic output. Manual assignments remain unchanged.

### D03 — make import preview, identity and ownership explicit

**Start at:** `Application/Kitaron/SqlServerKitaronSourceReader.cs`, `KitaronSyncService.cs`, existing mappings/import receipts and Kitaron tests.

Define stable source identity as source system + entity kind + external key, retaining existing mappings. Add mapping version, import-run identity, source snapshot/watermark and per-field ownership where missing. Acquire a supported consistent ERP snapshot; if consistency cannot be guaranteed, detect/report changed source and require a fresh plan instead of claiming a coherent snapshot.

Produce a preview of creates, mapped updates, unchanged records, ambiguous mappings, source deletions and conflicts. Store only bounded values/hashes needed for review/reconciliation. Apply only against the observed local and source/mapping context. Decide current automatic-sync behavior explicitly: ordinary safe mapped updates may remain automatic, while mapping changes/destructive or ambiguous cases need review. Never silently delete historical production evidence because an ERP row disappeared.

Keep imported stock/receipt facts separate from local material verification and batch allocation. Preserve parent/component demand provenance and avoid double counting parent demand and exploded children. Provide source-row links/details and units in error messages.

Acceptance: stable rerun is idempotent; changed mapping between preview/apply conflicts; ambiguous keys and unit mismatch rejected; source closes/deletes an old order while active local work exists; partially imported run rolls back/reconciles according to the documented atomic unit; parent/child demand counted correctly. Preview alone performs no write-back to ERP.

### D04 — reconcile artifact publication and define retention

**Start at:** package/release file stores, package repository, artifact options and startup registrations. Build on C04/D01 rather than adding a parallel package catalog.

Create durable publication state or a journal tying staging directory, expected context, immutable manifest/hash and final package record together. Startup/maintenance reconciliation distinguishes staged, published-unreferenced, active, superseded, missing and corrupt artifacts. Quarantine/report ambiguous files. Delete only demonstrably unreferenced data after a configured grace period and after excluding active builds/backup pins.

Define separate retention for production evidence, raw telemetry, rebuildable previews/projections, import staging and AI diagnostics. Preserve existing raw-telemetry retention. Use typed/indexed references for production queries; keep JSON details for provenance. Introduce indexes after checking existing ones and realistic query plans.

Acceptance: crash between each filesystem/SQL step; deterministic restart recovery; current and backup-referenced artifacts never collected; missing active artifact yields a visible health error; rebuildable cache can be deleted/rebuilt without changing authoritative quantities, assignments or releases.

### D05 — measure and remove demonstrated performance waste

Use a production-shaped test copy: 16 Machines, real calendar/dependency patterns, 200/1,000/5,000 active operations, independent history sizes, 1/3/5 Windows clients, TV/tablets, CNC ingestion, import, backup and package builds. These are test dimensions, not claimed factory volumes.

Instrument source SQL, timeline calculation, projection serialization, WPF apply/render, memory, SQLite busy/transaction time, request counts and ingestion lag. Record p50/p95/p99 and workload/configuration. Profile 7/30/180-day horizons and Jerusalem DST/holidays. Keep measured baselines with changes.

Batch current-package validity checks in `SqlitePreparationQueueRepository` instead of one query per operation. Coalesce identical in-flight projections and add bounded revision-keyed caching only after D02. Put an explicit configurable interactive horizon cap and a cancellable long-range report path in `TimelineEndpoints`. Bound aggregate NC/subprogram bytes and simultaneous builds; stream hashing/downloads where integrity permits.

Preserve existing client refresh guards and selection/scroll state. Do not hold SQLite locks while awaiting files, ERP or inference. Optimize large classes by extracting the touched cohesive responsibility, not by a broad cosmetic rewrite.

Acceptance: identical domain output before/after; realistic contention benchmark; cancellation frees work; bounded memory under maximum permitted package load; no unbounded date loop or request stampede. Proposed pilot goals: board/queue p95 server time <500 ms, ordinary refresh <1 s, and no >1 s ingestion lag from background AI/backup. Record actual values and justify any revised target before claiming acceptance.

### D06 — expose operational health and inspectable evidence

Add a server health projection with distinct timestamps/statuses for CNC observation freshness, ERP import/push outcome, tablet check-in/content revision, package integrity, last complete backup and optional AI runtime. “Connected” must not imply “fresh data,” and “last attempted” must not imply “last succeeded.”

Use existing local structured diagnostics with correlation/event/Run/package/plan IDs, duration and result code. Add bounded, cursor-based reads suitable for the UI and AI; never use a globally limited feed as accounting truth. Avoid logging NC content, credentials or complete prompts by default. Resolve links through server-authorized entity IDs.

Acceptance: disconnected CNC, stale ERP, interrupted backup, corrupt package, unknown push outcome and unavailable AI each produce a distinct readable state. Health reads do not mutate workflow. Existing TV/E-Ink scope is unchanged.

## 5. Industrial workbench implementation

### U01 — establish shared navigation, selection and visual tokens

Implement in existing WPF `Views/`, `Presentation/` and shared resources. Use four primary workspaces: Shop, Preparation, Operation, Integrations; keep Cases, detailed Timeline, Tool Catalog and Administration accessible. Rename administrative “Setup” to “Administration” to distinguish physical setup.

Create a shared selected-context object carrying Case/operation/batch/Run/program/output/Machine/package IDs as applicable. Navigation passes IDs and resolves fresh data, preserving source selection/scroll. No client-side authoritative readiness calculations.

Translate the companion concept into WPF tokens: graphite and light surfaces, restrained cyan selection, readable borders, monospaced identifiers, textual status and adequate contrast. Preserve mandated operational color meanings. Use actual shop data counts; never hard-code 16 as the domain limit. Keep E-Ink's flat white interface separate.

Acceptance: deep links land on the exact operation/Run and can return to source; theme switch preserves state; long names and IDs remain usable; existing commands remain accessible. Stage behind a reversible navigation setting while equivalent workflows are verified.

### U02 — implement preparation as facts plus approved human handoff

Extend `PreparationQueueService`, repository, `PreparationQueueViewModel` and `PreparationQueueView.xaml`. Display one actionable exact-context row with required-by, owner, Machine, missing/stale facts and package revision. Provide filters for NC, Tool Room, Setup and QC using the established queue interaction pattern.

After D3, expose separate facts for engineering readiness, package created, physical cart handoff and loader execution. If handoff is approved: authorized Windows user selects a qualified setup operator for the already assigned Machine; append actor/time/operator/package/context version atomically and idempotently. Qualification changes and superseded packages must not silently reuse an old handoff as current. Keep historical handoffs visible.

If D3 retains package-created→Ready-for-Setup, keep that transition and present handoff only as optional tracking if approved. Do not invent backfilled handoffs for existing packages. Download/copy/export never means physically handed over or setup started. No new manual setup-start button unless D4 explicitly calls for one.

Acceptance: same facts always reproduce same queue; material state visible; loader-generated and loader-executed distinct; unauthorized/duplicate/stale handoff requests handled correctly; operator qualifications enforced; exact package changes invalidate the appropriate current predicates; current QC/tablet semantics remain unchanged.

### U03 — build the operation/package workbench

Show breadcrumb and separate reusable recipe, quantity obligation and physical Run. Present readiness reasons, parent/order demand, material facts, tool/offset mode, Machine compatibility, current package manifest, creator/time, NC/tool revisions, loader ID, hashes/integrity and supersession history in one context.

Reuse existing upload/release/package/export commands. Display current vs historical package conspicuously; old packages can be inspected without appearing ready. A build conflict preserves draft context and offers refresh/review. Export displays the exact revision and records an existing permitted audit event only; it introduces no approval bureaucracy.

Acceptance: user can explain why work is blocked and identify the correct USB package without switching several tabs; split-Run selection is unambiguous; permission rules unchanged; missing source/artifact and stale package states actionable; opening NC uses read-only released content where appropriate.

### U04 — implement Shop and Integrations workspaces

Shop: compact Machine cards/rows with actual execution, counts, data freshness, next manually committed work and blocker/owner. Label predictions separately from observations. Selecting a Machine opens exact active/next context; unknown telemetry never becomes an invented idle/production state.

Integrations: ERP mapping preview from D03, direction and field ownership, current import/push results, conflicts/unknown outcomes, backup health, device freshness and AI availability. Preserve existing permissioned administration actions. Make read-only ERP mode explicit. Preview rows show source value, proposed local value, mapping rule and reason.

Acceptance: shop works at both typical and larger Machine counts; late telemetry and lost connection are distinguishable; stale projection is labeled; mapping preview matches applied plan/context; double-click cannot duplicate import/push commands; no real outward action occurs from a preview alone.

### U05 — verify accessibility and operator usability

Correct global Redo from Ctrl+X to the selected conventional shortcut, preserving normal Cut behavior in text fields. Test keyboard focus and all main actions without a mouse. Validate 1366×768 and 1920×1080 at 100/125/150% DPI, long labels, Hebrew RTL, Russian/English text, LTR identifiers inside RTL views and both themes. Require readable text/icons independent of color.

Run task-based review with planner, Tool Room and setup users: locate blocked work, identify missing fact/owner, create/open exact package, resolve stale-edit conflict and inspect ERP mapping. Record failures and fix navigation before acceptance. The HTML mock's script checks are not WPF visual acceptance.

## 6. Local AI implementation

### Target architecture and MVP boundary

Implement a server-owned **AI gateway** and bounded background request pump in the existing .NET server; run the model in a **separate local Ollama process**. Start with loopback. The same adapter may later target an explicitly configured factory-LAN GPU host. A custom additional Python service, vector database and agent framework are not prerequisites.

Data flow: signed-in Windows request → server authorization/context resolution → deterministic evidence bundle → bounded inference queue → local runtime → schema/evidence validation → stored result metadata → WPF answer with record links. Live quantities, eligibility and timeline mathematics come from normal server queries. Model inference never runs in a DB transaction or receives a live SQLite path.

MVP supports three bounded intents: `ExplainBlockers`, `SummarizeShift`, and `FindSimilarIncidents` (the third after A04). It does not execute arbitrary tools, generate SQL, browse the Internet, read arbitrary folders or modify shop state. User text and retrieved text cannot expand the allowed scope. Store answer source IDs and versions so later data changes do not make an old answer look current.

Proposed locations, to adjust to existing conventions: `Application/LocalAi/`, `Api/LocalAi/`, `Configuration/LocalAiOptions.cs`, server-owned persistence/migrations, Windows `Presentation/LocalAiPanelViewModel.cs` and `Views/LocalAiPanel.xaml`, with corresponding server and Windows tests. These are new planned components, not existing files.

### A01 — implement evidence contracts and deterministic explanations

Create a typed evidence service using existing domain services: Run context, action-specific readiness, recent scoped events, package manifest, timeline conflict explanation and health. Each fact includes entity type/ID, source record/event, value/unit, observed time, server received time, relevant version and validity/freshness. Use UTC instants plus explicit factory timezone for shift interpretation; test ambiguous/nonexistent DST times.

Restrict scope from the authenticated Windows user and requested context. Recheck source visibility when displaying saved answers and opening evidence. An answer cache must include permission/scope version and data revision; cross-user cache reuse cannot broaden access. TV/E-Ink credentials do not acquire a general AI endpoint.

Compute blockers, quantities, date ranges and comparisons deterministically. Return a structured explanation without a model as a fallback. Define evidence IDs unique within a bundle, and let the server map them to safe application routes. Do not let the model invent URLs or file paths.

Illustrative contract shape (proposed, names may follow existing API conventions):

```json
{
  "schemaVersion": 1,
  "requestId": "server-issued-id",
  "intent": "ExplainBlockers",
  "asOfUtc": "2026-10-08T05:00:00Z",
  "planRevision": "opaque-version",
  "context": {"runId": "existing-run-id", "machineId": "existing-machine-id"},
  "facts": [
    {"evidenceId": "E1", "kind": "ReadinessGate", "code": "MATERIAL_NOT_VERIFIED",
     "value": false, "sourceRecordId": "existing-record-id", "sourceVersion": "opaque-version"}
  ],
  "knownLimitations": ["Machine observations are stale"]
}
```

Acceptance: same context/revision gives equivalent facts; no unauthorized data; cursor/time-range bounds enforced; old/new package and observed/forecast states distinct; missing facts explicit. Domain quantities and durations match normal APIs exactly.

### A02 — add optional local runtime and bounded request lifecycle

Implement a small `ILocalModelClient` adapter, first for Ollama's documented `POST /api/chat`. Supply an explicit configured model artifact and JSON schema, disable streaming initially, and collect runtime/token/latency metadata. Keep runtime-specific request details behind the adapter. Pin the tested runtime version and model digest; no silent automatic model upgrades or downloads during a user request.

Register the gateway as optional, disabled by default for installation compatibility. Proposed application-owned settings (not claims that these are Ollama environment variable names):

| Setting | Initial pilot value / behavior |
|---|---|
| `Enabled` | false until deployment evaluation passes |
| `Provider` / `BaseUrl` | Ollama / `http://127.0.0.1:11434` |
| `ModelId` / `ModelDigest` | Required from A07; no fabricated production default |
| `AllowedIntents` | ExplainBlockers, SummarizeShift; historical search later |
| `MaxConcurrentRequests` / `MaxQueuedRequests` | 1 / 8 |
| `MaxQueueWaitSeconds` / `RequestTimeoutSeconds` | 30 / 60, measured and adjusted explicitly |
| `MaxInputTokens` / `MaxOutputTokens` | Pilot budget 8,192 / 1,024, subject to chosen model; include prompt/schema and reserve output space |
| `MaxQuestionCharacters` | 2,000; return a readable validation message |
| `StorePromptBodies` | false |
| `RequestMetadataRetentionDays` | Pilot proposal 30; cleanup bounded; evidence retention remains independent |

Use one bounded queue with fair per-user limits, cancellation and backpressure. Persist request metadata/result as needed in server-owned tables, not the model process. On server restart, mark in-flight requests interrupted; do not replay old contexts silently. Stop work when its result is no longer needed. A user cannot choose arbitrary model endpoints; validate operator-configured destinations and avoid redirect-based escape from the local deployment boundary.

Use local-only runtime configuration (`OLLAMA_NO_CLOUD=1` as documented), loopback bind initially, no runtime access from tablets or browser clients, and verify operation with external network unavailable. A remote factory AI host is an explicit network deployment with restricted access; this does not change CNC/tablet identity rules.

Acceptance: disabled/missing runtime does not affect Planner startup; queue full gives a bounded response; timeout/cancel releases capacity; restart records interruption; malformed/oversize response rejected; model missing is a configuration error, not an automatic Internet download; no cloud fallback. Under load, DB/telemetry latency stays within the agreed target.

### A03 — validate output, evidence and factual claims

Require a strict response schema. Let the model refer to supplied fact IDs and write explanations/suggestions; it must not become the source of production values. Example application result structure:

```json
{
  "schemaVersion": 1,
  "status": "Answered",
  "summary": "Material verification is still missing.",
  "claims": [{"text": "Material is not verified for this work.", "evidenceIds": ["E1"]}],
  "suggestions": [{"code": "OPEN_MATERIAL_READINESS", "evidenceIds": ["E1"]}],
  "missingEvidence": [],
  "limitations": []
}
```

The server supplies request ID, model identity, timestamps, source versions and navigation actions outside the model-generated body. Validate enum values, lengths, all cited IDs and allowed suggestion codes. Display quantities/state values from typed evidence cards, not by trusting arbitrary model prose. Reject claims contradicting typed facts; unknown or unverifiable claims must not be displayed as verified.

Schema conformity and valid citation IDs alone do not prove that a sentence follows from the evidence. Limit MVP claim templates/categories where possible, label remaining narrative as AI explanation, and measure unsupported statements in A07. On validation failure, return the deterministic fact summary or “insufficient evidence.” Allow at most one bounded formatting repair; never an open-ended retry loop.

Treat retrieved logs/documents as untrusted text, not instructions. Tests must include instructions embedded in filenames, notes and logs asking the model to ignore its scope or expose unrelated records. Do not create an execution path for generated commands. Escape rendered content and whitelist navigation actions; no model-supplied shell, SQL, URL or file-open command.

Acceptance: fabricated ID, contradictory quantity, invalid schema, stale context, malicious document, unauthorized record, overlong output and repeated model failure. All evidence links resolve only to the supplied authorized source; failure never causes a production mutation.

### A04 — implement bounded local incident and document retrieval

Start with indexed server logs/operational event summaries and approved local decision documents. Use exact ID/keyword/FTS search first. Direct structured queries remain the path for current Run counts and readiness. Add embeddings only after retrieval evaluation shows a material benefit.

An optional retrieval index is a rebuildable store owned through server/gateway code, separate from authoritative planning records. Store document/event identity, content hash/version, chunk boundaries, source visibility and ingestion time. Support deletion/tombstones, changed-source reindexing, index version and rebuild. Recheck visibility and source currency when a result is supplied to the model.

Use an explicit approved corpus/root list. Do not recursively ingest the whole engineering share or drawings/NC by default. Ignore executables, unsupported/binary content and oversized files. Retain links back to exact source sections and revision; similar incident does not imply current Machine/revision compatibility. Document parsing is read-only and cannot alter originals.

Acceptance: exact part/Run IDs found; changed/deleted document removed; old revision labeled; unauthorized corpus excluded; malformed file isolated; mixed Hebrew/Russian/English queries tested; complete index deletion/rebuild does not affect planning. No external embedding API.

### A05 — implement blocker explanation and shift summaries

`ExplainBlockers`: resolve selected operation/Run, fetch coherent readiness/package/material/health facts and produce a short answer: blocker, evidence, next responsible role and existing action to inspect. If responsibility is unknown, say so; do not invent an employee assignment. Distinguish missing evidence from a negative fact.

`SummarizeShift`: server resolves requested date/shift/timezone and computes counts, durations and changes from complete scoped sources. Report exception groups, stale devices, QC returns, package supersessions and unresolved integration outcomes with event links. Use bounded cursor-based aggregation, not “last N global events.” The model summarizes deterministic aggregates and representative evidence; totals remain computed by code.

`FindSimilarIncidents`: after A04, return prior incidents with Machine/context/revision and match rationale. Present historical resolutions as references requiring applicability review. No automatic task assignment or command execution.

Acceptance: known blocker fixture explained correctly; incomplete material data acknowledged; duplicate events not double-counted; shift crossing midnight/DST handled; summaries reconcile exactly to server aggregates; concurrent package change marks previous answer stale; empty shift and contradictory observations handled honestly.

### A06 — add the WPF AI panel

Place “Explain blockers” in the operation/package workbench and a shift-summary entry in Shop/health. Display queued/running/cancelled/unavailable states, answer as-of time, evidence links, missing facts and a clear AI label. Show deterministic facts immediately if useful while inference is queued.

Actions only navigate to existing authorized screens. Opening “material readiness” does not approve material; viewing a suggestion never reorders backlog or releases a package. On context change, cancel or clearly retain the old result under its original context. Mark stale answers when source versions change. User feedback records useful/not useful and an optional short correction without altering source facts.

Acceptance: cancellation and navigation during generation; permission change before opening old answer; Hebrew RTL/Russian; keyboard access; offline runtime; stale/superseded package; clear deterministic fallback. Ordinary workbench remains usable with AI disabled.

### A07 — select the actual model and package the deployment

Do not hard-code a model chosen only by parameter count. Inventory the real Windows server/optional GPU host. Record CPU, RAM, GPU/VRAM, disk, service identity and expected concurrent use. Evaluate at least two locally runnable instruction-model artifacts with license and deployment suitability checked, using the same evidence fixtures, quantization and context limits. Pin exact digest, runtime version and configuration in the result.

Planning envelope only: existing 32–64 GB RAM machine for proof of value; a dedicated 64 GB RAM, 16–24 GB VRAM class host for quantized 7–14B candidates if needed. These are not purchase requirements or guaranteed fit. Benchmark before recommending hardware. Increase model size only when smaller candidates fail quality criteria and the cost is justified.

Create 50–100 labeled evaluation questions covering Hebrew, Russian and English; real shop vocabulary and mixed-direction IDs; all readiness modes; superseded loader/package; duplicate events; telemetry gaps; conflicting records; no evidence; prompt injection; authorized/unauthorized history. Each fixture specifies expected facts, acceptable uncertainty and source IDs. Add repeat runs to expose variability.

Report citation correctness, unsupported factual claims, identifier accuracy, language usefulness, retrieval quality, schema failure rate, warm/cold latency, memory and Planner ingestion impact. Pilot acceptance: zero invented Machine/Run/package identities and unauthorized records; deterministic numbers exactly match; every displayed citation resolves; no critical unsupported actionable claim; at least 90% human-rated correct/useful answers on answerable fixtures; no invented answer on designated insufficient-evidence fixtures. If the model fails, retain deterministic fallback and do not enable the failed intent. Targets are evaluation gates, not current results.

Provisional performance target: warm short blocker explanations p95 ≤15 s on the selected host, bounded by A02 timeout; measure and separately report cold start. Keep Planner ingestion lag within D05 limits. A faster model that invents facts does not pass.

Deliver scripts/runbook for local runtime install, approved model acquisition, digest validation, offline startup, service recovery, model rollback and uninstall. Test Windows Service identity and reboot behavior on the target host. Separate model files from source/git and production DB backup; retain enough manifest/configuration to reproduce deployment. No live test requires the real CNC to move or ERP to write.

### A08 — conduct a small monitored pilot and stage expansion

Enable for a few Windows users on selected work contexts after R01. Start with blocker explanations, then shift summaries. Compare output to real records and collect feedback for an agreed pilot period, suggested two work weeks. Measure answer usefulness and actual workflow time saved, not only token throughput.

Keep a visible disable switch that stops new requests and cancels/drains existing work without changing Planner state. Review rejected outputs, stale-context frequency, queue saturation, memory and impact on ingestion. Log minimal metadata by default; temporary diagnostic prompt capture needs explicit scope/retention and remains local.

Expansion candidates are human-reviewed ERP mapping suggestions and explanations of deterministic NC/tool revision differences. They are follow-on tasks only after pilot acceptance. NC generation, automatic offsets, autonomous scheduling and machine control remain outside this handoff.

Acceptance: documented go/no-go results, measured host load, working rollback, selected model/version recorded, unresolved risks assigned and no production-state changes attributable to AI responses.

### Verified runtime references

Official documentation checked 8 October 2026: [Ollama chat API](https://docs.ollama.com/api/chat) documents chat requests and structured-format options; [structured outputs](https://docs.ollama.com/capabilities/structured-outputs) documents JSON-schema output; [FAQ](https://docs.ollama.com/faq) documents local-only operation and configuration. Recheck exact deployed version at A07. These sources establish runtime capabilities, not model quality or factory performance. All queue sizes, budgets, gates and architecture above are proposed application requirements.

## 7. Release, testing and traceability

### R01 — rehearse each release on representative isolated data

Use a Windows-capable environment with the repository's required .NET SDK/native dependencies. The audit environment could not run .NET/WPF; do not inherit a false green baseline. Discover actual test names before filtering. Representative entry commands:

```powershell
dotnet test tests/Meimad.Planner.Server.Tests/Meimad.Planner.Server.Tests.csproj
dotnet test tests/Meimad.Planner.Client.Windows.Tests/Meimad.Planner.Client.Windows.Tests.csproj
```

Run focused tests for each change, then the relevant full suites at the release gate. Include migrations from empty and representative older/current DBs; concurrency barriers that deliberately interleave operations; filesystem/SQL/network fault injection; import dry-run; backup/restore; API permissions; multilingual UI and performance workload. Use existing simulators for verification before approved physical commissioning. Keep production credentials/data out of committed fixtures.

For schema-changing deployment: complete pre-upgrade backup set; stop/quiesce writes where required; migrate with the supported mechanism; verify domain totals/current pointers and artifact integrity; start server; upgrade incompatible Windows clients; verify TV/E-Ink compatibility. Rollback may require the whole prior backup set and matching binaries. Do not restore an older DB while leaving a mixture of incompatible package/configuration state; reconcile any real production facts generated after the snapshot before returning to service.

AI deploy/disable is independent of the core schema rollout where possible. Simulate model outage, queue pressure and host reboot during normal Planner activity. Production enablement follows measured acceptance, not merely a merged PR.

### R02 — keep the implementation record current

Update affected sections of `docs/functional-spec.md`, `docs/api-contract.md`, `docs/data-model.md`, `docs/architecture.md`, `docs/implementation-plan.md` and production-run/package documentation. Update `AGENTS.md` only for accepted lasting rules; resolve its stale deferred-feature claims using current owner decisions rather than deleting boundaries indiscriminately.

Add a concise local-AI design/deployment document, model manifest and evaluation report. Keep credentials, real logs, DBs, generated packages, model weights and indexes out of git. Mark task status and link the implementing commit/PR. Preserve prior task requirements unless explicitly superseded.

Completion report template for each task/release:

1. Task IDs completed and base/current commit.
2. Behavior changed and files/contract/migration affected.
3. Tests run, actual results and remaining environmental checks.
4. Measured performance/restore/model results where applicable.
5. Compatibility, rollout and rollback instructions.
6. Unresolved decisions and the exact tasks they block.

### Audit coverage

| Audit finding or requested area | Implementation tasks |
|---|---|
| F1 global timing window | C01, D02 |
| F2 stale pin overwrite / F3 invalid pin ownership | C02 |
| F4 package publication concurrency | C04, D04 |
| F5 arbitrary Run binding | C03 |
| F6 readiness inconsistency | C05, U02, U03 |
| F7 duplicate cycle target mismatch | C06 |
| F8 ERP concurrent edits | C07 |
| F9 unknown ERP commit outcome | C08, U04 |
| F10 incomplete recovery scope | D01, D04, R01 |
| F11 mixed projection/source snapshots | D02, D03 |
| Performance and maintainability | D05, D06 |
| Data flow, storage and retention | C01, C08, D01–D04 |
| Unclear UI and industrial concepts | U01–U05 |
| Local model, evidence, deployment and evaluation | A01–A08 |

### First executable batch

Start with P00, then C01, C02, C03 and C06 as separate changes. Follow with C04 and D01. Resolve D2/D3 while those repairs proceed, then implement C05 and preparation UI semantics. If ERP push is in use, bring C07/C08 into this first release. Establish D02/A01 contracts next so local AI can be developed without guessing at domain truth.
