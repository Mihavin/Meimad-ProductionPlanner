# Performance and stability audit - 2026-08-23

## Outcome

The Release solution builds without warnings, the analyzers are clean, package vulnerability auditing reports no known vulnerable NuGet dependency, and the complete automated suites pass after the audit fixes. The application is stable under the exercised test and read-only concurrent-load paths, but it is not yet accurate to describe the complete multi-output Production Run experience as finished. The factory service also has a deployment/configuration mismatch that keeps the Haas connector offline even though its MTConnect agent is reachable.

## Scope and method

The audit covered Server, Windows client, Planning Board drag/drop, Timeline projection/rendering, localization and tab switching, schema migrations through v47, Production Run calculation/execution, MTConnect parsing, package dependencies, installer artifacts, and read-only measurements against the locally installed factory service. No factory planning data or connector configuration was changed. A copy of the database was used when current-code diagnostics were required.

## Verification summary

| Check | Result |
| --- | --- |
| Release solution build | Passed, 0 warnings, 0 errors, 4.74 s warmed build |
| .NET analyzer verification | Passed |
| Server suite | 520 passed, 0 failed |
| Windows suite | 229 passed, 0 failed |
| NuGet vulnerability audit | No known vulnerable direct or transitive packages in all four projects |
| Diff whitespace validation | Passed; line-ending conversion notices only |
| Schema startup | Database copy upgraded cleanly through v45, v46, and v47 |
| Installer artifacts | Pre-audit Client and Server v0.1.34 MSIs present with the documented hashes; rebuild required to include audit repairs |

## Focused test timings

These are wall-clock measurements on the audit workstation and include `dotnet test` process startup. They are regression indicators, not production SLAs.

| Area | Tests | Wall time | Result |
| --- | ---: | ---: | --- |
| Timeline calculation engine | 48 | 1.448 s | Passed |
| Production Run | 18 | 2.694 s | Passed |
| MTConnect | 33 | 8.208 s | Passed; timeout/failure cases intentionally wait |
| Migrations | 22 | 7.977 s | Passed |
| Planning Board view model | 23 | 1.332 s | Passed |
| WPF startup, Timeline rendering, STEP visibility, tab/language stress | 1 integrated audit | 8.423 s | Passed |

The WPF audit exercises every nested tab, rapid English/Hebrew/Russian changes, dispatcher heartbeats, coalesced localization passes, Timeline visual batching, hidden STEP rendering, external Timeline lifetime, and timer cleanup. Its internal budgets remain 750 ms per interaction and 8 seconds per measured rapid-switch phase.

## Factory-sized read-only measurements

The installed service is v0.1.33. Its measured dataset contained 16 Machines, 55 Batch Operations, 33 assignments, 14 active resources, 2 downtime windows, 356 projected intervals, 18 returned dependencies, and 33 Timeline conflicts over the tested 30-day horizon.

| Endpoint/workload | Measurement |
| --- | --- |
| Health, cold sample | 158 ms, HTTP 200 |
| Planning Board, 5 sequential samples | 93-234 ms, median 114 ms, 97,161 bytes |
| Timeline, 5 sequential samples | 1,243-1,358 ms, median 1,322 ms, about 1.0 MB |
| 10 concurrent Planning Board reads | All HTTP 200 in 263 ms wall time |
| 4 concurrent Timeline reads | All HTTP 200 in 1,378 ms wall time |
| Haas MTConnect `/current` | 78 ms, HTTP 200, 63,800 bytes |

Current v0.1.34 code against a database copy logged 21 ms for the Timeline SQLite snapshot, 466 ms for the primary engine, 539 ms for the missed-forecast baseline engine, and 1,205 ms total server projection work. The approximately 2.4 s cold HTTP observation also included startup/JIT and concurrent connector diagnostics. The double engine calculation, not SQLite, is the dominant Timeline cost.

## Findings

### High - installed CNC authority does not use the working MTConnect provider

The Windows service is v0.1.33 while the current verified installer/build is v0.1.34. The authoritative unified Haas connection returned:

- telemetry provider `MDC`;
- no MTConnect configuration;
- production variable `#10605`;
- part counter source `Q500`;
- status `OFFLINE` and no successful poll.

The same machine's MTConnect `/current` endpoint returned HTTP 200. Current-code diagnostics reproduced repeated Q500 failures because the response did not contain a valid `PARTS` value. TCP reachability therefore does not make the connector healthy: the Server is selecting the wrong provider/configuration. Install v0.1.34, then explicitly save the Haas configuration with `MTCONNECT`, port `8082`, and the intended production variable (the shop-floor value previously specified as `#10699`). Confirm the unified CNC connection response before relying on the TV indicator. Installation alone must not be assumed to rewrite an already authoritative connector choice.

### High - multi-output Production Run UI projections are incomplete

The Server Planning Board response includes `productionRuns`, but the Windows `MachinePlanningBoardViewModel` does not consume that collection and the XAML has no Production Run card surface. The Windows client can open the create dialog, but it continues to render and drag legacy operation cards. Likewise, the Server Timeline model emits Production Run projections while the Windows Timeline contract/view ignores them. TV and E-Ink projection code has no Production Run references.

Consequently, API/persistence/planner tests prove the aggregate rules, but do not prove the task-pack acceptance requirement that one Run is the visible schedulable unit across Planning Board, Timeline, TV, and E-Ink. `docs/production-run-verification.md` should not be treated as end-to-end UI acceptance until these consumers and UI tests exist.

### Medium - Timeline exceeds the repository's investigation threshold

The existing performance guidance says to investigate server projections above 250 ms. The factory-sized 30-day projection has a 1.322 s median. Source loading is only about 21 ms; the primary and baseline calculations account for about 1.0 s. The baseline is required for current missed-start warnings, but approximately doubles CPU for this data. Recommended next work is to avoid a second full graph calculation, retain a prior non-authoritative forecast snapshot, or bound/debounce the default client horizon while preserving dependency correctness.

### Medium - Production Run Planning Board projection scales with N+1 reads

`SqliteProductionRunPlanningProjectionRepository` lists Runs and then calculates readiness for each Run. Readiness re-reads the Run, reads each output's legacy operation readiness separately, and reads combined tooling separately. Latency and connection/query count therefore grow with Runs and outputs. Only one Production Run exists in the measured factory database, so the current 114 ms Planning Board median does not stress this path. A bulk snapshot query should replace the per-Run/per-output reads before broad multi-output adoption.

### Medium - Planning Board image loading adds unmeasured client fan-out

After every board refresh the Windows client downloads Machine pictures sequentially, launches one preview request per distinct Case without a concurrency bound, and separately reloads all Case masters to resolve preview paths. The HTTP Planning Board measurements exclude this work. Add keyed image caching and bounded concurrent fetches, and invalidate cache entries when paths/versions change.

### Medium - automated coverage does not exercise the actual drag/drop routed events

View-model move behavior is covered, but prior tests did not drive WPF drag source/drop routed events. This audit added an STA regression for the content-element ancestor path and hardened drag initiation. A future UI automation layer should also simulate repeated cross-column drops, canceled drags, stale versions, and unload during drag.

### Low - service health is process-only

`/health` reported healthy while the authoritative CNC connection was offline. This is useful as a process liveness endpoint, but it is not factory integration readiness. Operational monitoring should display connector/database/report dependency health separately rather than redefining process health.

## Repairs made during this audit

1. Planning Board drag startup now prevents nested drag initiation, safely walks both WPF visual and content/logical parents, catches drag-loop failures, reports them in the board status, and always clears drag state.
2. Added an STA regression proving the previously unsafe inline/content-element ancestor path does not throw.
3. Production Run Planning Board readiness no longer swallows request cancellation. Non-cancellation projection failures are logged with the Run ID before the card is conservatively marked not ready.

## Residual acceptance work

Before calling the whole application production-ready for multi-output Runs:

1. Build the next installer containing these audit repairs, deploy it, and re-save/verify the unified MTConnect configuration; confirm successful live polls and `#10699` mode changes.
2. Implement and test Production Run cards and drag/reorder behavior in the Windows Planning Board.
3. Consume/render Production Run occupancy and completion projections in the Windows Timeline.
4. Add Production Run/current-program/output projections to TV and E-Ink read models.
5. Replace Production Run readiness N+1 queries with one bounded database snapshot and add a factory-scale budget test.
6. Profile and remove the second full Timeline engine pass, or formally accept a measured latency budget for the 30-day factory view.
