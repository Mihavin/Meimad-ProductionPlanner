# Timeline performance instrumentation and profiling report

## Auxiliary reservation indexes (2026-10-08; follow-up)

A controlled 5,000-operation shop profile (1,000 archived QC events, two samples for each of 1/3/5 readers) identified auxiliary scheduling as the largest measured phase. Calculation-local reservation endpoint sets, per-resource lists and cached calendar intersections preserve every original candidate boundary and duplicate load entry. Assignment/dependency lookups also use stable IDs. No Machine/backlog, capacity, pin, timing or tie-breaking rule changes.

| Readers | Before Board p50 / p95 ms | Indexed Board p50 / p95 ms | Before / indexed auxiliary p50 ms |
|---|---:|---:|---:|
| 1 | 15,247 / 32,768 | 12,149 / 19,007 | 7,304 / 1,631 |
| 3 | 15,045 / 15,305 | 9,908 / 10,448 | 7,175 / 1,621 |
| 5 | 15,386 / 15,432 | 9,878 / 9,902 | 7,311 / 1,620 |

Evidence: `artifacts/board-5000-phase-before.json`, `board-5000-phase-indexed.json`. All HTTP requests succeeded. These small sample tails are observations, not acceptance; the proposed Board target remains unmet. Twenty-four focused tests passed, including 64 complete result signatures captured from the pre-index allocator (0.1.189 source on .NET 10) and unchanged input checks. Fixture coverage includes both directions, dependencies, pins, capacities, multiple calendars, missing skills, zero-duration work, duplicate/unrelated reservations, external lead times and ordering variations. The snapshots protect existing behavior, not a claim that every historical scheduling policy is ideal. The subsequent phase measurements and completed regression are recorded below.

Further phase instrumentation isolated another 4.4 seconds in global assignment-identity normalization. Its diagnostic pass rescanned all public intervals for every active assignment. One ordinal assignment-ID grouping now supplies the same duplicate/missing counts. With both changes, Board p50/p95 were **6,452/6,780 ms** at one reader, **6,555/12,929 ms** at three, and **6,517/6,672 ms** at five; identity normalization p50 fell to 254/141/109 ms respectively. Evidence: `artifacts/board-5000-identity-before.json`, `board-5000-identity-indexed.json`. The intermediate phase reports distinguish total projection, mapping, presentation, identity, readiness and final assembly; nested phases must not be added to their parent totals. Every request succeeded; these results still miss the proposed target.

Final focused regression: **35 passed**, zero failures/skips. Calendar reuse is capped at 1,024 pairs/65,536 windows with uncached fallback; a 1,225-pair test preserves stable ties and verifies that a later calculation observes changed calendars and no previous reservations. The complete Release Server regression passed **1,352 tests, five opt-in skips, zero failures**. Both **0.1.190** installers built with zero warnings/errors and verified checksums; 0.1.189 predates these optimizations. See the implementation plan for exact hashes and logs.

The final 5,000-operation mixed repeat passed all 24 Board reads, 24 queue reads, 100 diagnostic appends, two package builds and complete verified recovery. Board p50/p95 were **6,398/11,058 ms**, versus **15,065/31,871 ms** in the budgeted pre-index run. Queue p50/p95 stayed **710/784 ms**; diagnostic appends were **1.59/4.89 ms**. The single blocker request/source recheck returned bounded unknown forecast in **6,169 ms**, versus 14,555 ms before; shift summary was current in 232 ms. Recovery verification took 1,140 ms. Evidence: `artifacts/workbench-indexed-mixed-5000.json`. Forecast remained `CHECK_UNAVAILABLE` under its budget; no equally complete forecast speedup, model performance or factory acceptance is claimed.

## Mixed reads, diagnostics, packages and recovery (2026-10-08)

### Maximum supported synthetic workload and forecast budget

The same three-reader mixed harness was run at **5,000 operations**, with 1,000 archived QC events. Both runs completed all 24 Board reads, 24 Setup queue reads, 100 diagnostic appends, two package builds and complete recovery verification. The first blocker explanation/source recheck took **72,157 ms**, exposing excessive optional forecast work. A configurable three-second forecast budget now preserves recorded/readiness evidence and marks timed-out forecast evidence unknown; it does not fabricate a conflict-free result or silently discard caller cancellation.

| 5,000-operation observation | Before budget | With budget |
|---|---:|---:|
| Board p50 / p95 ms | 14,019 / 33,796 | 15,065 / 31,871 |
| Setup queue p50 / p95 ms | 701 / 861 | 704 / 785 |
| Diagnostic append p50 / p95 ms | 1.58 / 4.34 | 1.57 / 3.05 |
| Single blocker request + source check ms | 72,157 | 14,555 |
| Single shift summary ms | 120 | 140 |
| Complete backup/verified restore ms | 1,190 | 1,106 |

The budgeted blocker result was `DETERMINISTIC`, `CHECK_UNAVAILABLE`, `timeline.budget_exceeded`; the summary was `CURRENT_AT_CHECK`. The improvement is bounded partial evidence, **not an equally complete forecast produced faster**. Single AI samples do not establish a p95/model SLA. Board/queue targets are still unmet at this size. These are synthetic diagnostic writes, not measured CNC ingestion lag. Before-budget wall time was 147.8 s, cumulative allocation 61.3 GB and final working set 688 MB; cumulative allocation is not retained memory. Evidence: `artifacts/workbench-evidence-mixed-5000.json`, `workbench-budgeted-mixed-5000.json` and corresponding logs. Factory deployment/performance acceptance remains open.

A subsequent 1,000-operation run retained forecast evidence: its single blocker request/recheck returned `DETERMINISTIC|CURRENT_AT_CHECK|forecast:included` in 5,428 ms; the summary returned current evidence in 134 ms. Every read/write/build/recovery operation succeeded. Evidence: `artifacts/workbench-budgeted-mixed-1000.json`. The budget therefore remains a measured fallback for the larger workload, rather than unconditionally removing forecast evidence.

Follow-up with expanded operational evidence also runs one blocker explanation (including the current factory Timeline and a source recheck) after the shift summary. At 1,000 operations, all 24 Board/24 queue requests succeeded, all 100 writes were stored, both packages were built and recovery was verified. Board p50/p95: 929.1/2,784.1 ms; queue: 141.1/304.0 ms; diagnostic writes: 1.5/2.9 ms. The single deterministic summary took 155.1 ms, the single blocker request/recheck 5,557.7 ms, and recovery 818.6 ms. These single AI samples are not p95 estimates or model benchmarks. Board's target remains unmet; the additional forecast has visible CPU cost. Evidence: `artifacts/workbench-evidence-mixed-1000.json` and log. This extends the earlier workload and is not a controlled speedup comparison.

`scripts/profile-planner-mixed.ps1` measures three concurrent readers (eight Board/Setup queue pairs each), 100 diagnostic event writes spaced 100 ms apart, two package builds, one complete encrypted local backup/verified restore and one disabled-AI summary. It uses the existing 16-Machine shop fixture, 1,000 retained QC events, disposable paths and Release TestServer. It reports result codes as well as latency, allocations and working set; it does not turn a fast error into a successful measurement.

The first 200-operation run exposed projection starvation: **24/24 Board requests returned 503** while all preparation reads succeeded. Schema v108 fixes the demonstrated cause by retaining a conservative planning revision but excluding audited non-planning diagnostics and AI bookkeeping. Manual move/report events remain planning inputs; AI aggregates validate the full event revision. Tests deliberately append diagnostics or change assignments between Board repository reads to verify that distinction.

| After v108 | Operations | Successful reads | p50 ms | p95 ms | p99 ms |
|---|---:|---:|---:|---:|---:|
| Board | 200 | 24/24 | 217.9 | 1,448.8 | 1,448.8 |
| Setup queue | 200 | 24/24 | 51.5 | 81.4 | 81.4 |
| Diagnostic append | 200 | 100/100 | 1.4 | 3.2 | 9.5 |
| Board | 1,000 | 24/24 | 829.7 | 2,578.0 | 2,583.8 |
| Setup queue | 1,000 | 24/24 | 155.7 | 233.2 | 233.3 |
| Diagnostic append | 1,000 | 100/100 | 1.5 | 2.7 | 4.8 |

Both package builds succeeded in each run (p95 214.8/186.0 ms). Complete local backup/restore took 763.8/787.3 ms; deterministic AI completed in 126.9/143.2 ms. Wall time was about 10.93 s. Total process allocations were 0.84/4.72 GB (cumulative allocation, not retained memory); final working sets were 223.7/293.1 MB. The 200-operation repeat overlapped the tail of focused tests; the 1,000-operation run did not overlap another test/build. Cold first reads and real package mutations are included, unlike the earlier warmed read-only series. Small sample counts and shared-process measurement limit statistical interpretation.

The Board's proposed **p95 <500 ms remains unmet**. Diagnostic repository latency is not real CNC ingestion lag. This harness does not exercise real CNC/SQL Server/NAS, Windows Services, physical clients/devices or model inference. Evidence: ignored `artifacts/workbench-mixed-load-200.json`, `workbench-mixed-load-200-scoped.json`, `workbench-mixed-load-1000-scoped.json` and their test logs.

## D05 shop workload and retained-history dimension (2026-10-08)

The profiler now supports `-Workload independent|shop` (default independent) and `-HistoryEvents 0..100000` independently of active operation count. Shop counts must be multiples of four. Its four-operation batches rotate through independent, sequential, parallel-capable and locked-simultaneous routes on 16 manually assigned active Machines. Four setup workers are qualified for those Machines; two operators, one skilled QA worker, two capacity-one benches and an external lead-time step provide auxiliary scheduling work. Calendar availability follows Jerusalem weekdays and includes a synthetic October 15 closure. The long horizons span DST, but the measured operation set finishes before that transition; this is not DST scheduling acceptance.

The workload has one managed root recipe and three unmanaged legacy route steps. Twenty root-operation packages are created for the measured 200/1,000-operation cases. History consists of alternating SEND_TO_QC/QC_PASS workflow records on one completed Run, separate source operation and inactive Machine. Thus the `machines: 16` dimension counts active assigned Machines; history adds one inactive Machine. The archived Run intentionally has no actual timing evidence and reports `actual_history_missing`. This is retained QC-event volume, not broad historical Run diversity or a CNC cycle-observation workload.

All runs used Release TestServer and five rounds per 1/3/5-client group, sequentially without competing builds/tests. This change adds profiling infrastructure, not a runtime optimization, so these are workload observations rather than before/after speedup claims.

| Route | Operations | QC events | Clients | p50 ms | p95 ms | p99 ms |
| --- | --- | --- | --- | --- | --- | --- |
| Board | 200 | 0 | 1 | 228.6 | 289.5 | 289.5 |
| Board | 200 | 5,000 | 1 | 258.6 | 281.1 | 281.1 |
| Board | 200 | 5,000 | 3 | 240.9 | 263.4 | 263.4 |
| Board | 200 | 5,000 | 5 | 170.0 | 232.9 | 232.9 |
| Board | 1,000 | 5,000 | 1 | 750.0 | 782.5 | 782.5 |
| Board | 1,000 | 5,000 | 3 | 791.6 | 941.2 | 941.2 |
| Board | 1,000 | 5,000 | 5 | 800.3 | 906.0 | 906.1 |
| Live 30-day Timeline | 200 | 5,000 | 1 | 251.7 | 294.9 | 294.9 |
| Live 30-day Timeline | 200 | 5,000 | 3 | 251.6 | 290.9 | 290.9 |
| Live 30-day Timeline | 200 | 5,000 | 5 | 212.2 | 240.2 | 240.3 |
| Live 30-day Timeline | 1,000 | 5,000 | 1 | 819.6 | 848.6 | 848.6 |
| Live 30-day Timeline | 1,000 | 5,000 | 3 | 818.6 | 913.8 | 913.8 |
| Live 30-day Timeline | 1,000 | 5,000 | 5 | 821.7 | 840.5 | 841.2 |

The 7/30/180-day explicit-asOf runs also passed. Their repeated requests predominantly hit the existing completed-response cache and do not measure calculation throughput. Timeline output now records warmup projection shape: all 200/1,000 active operations received intervals, with 111/561 dependency links and 50/250 intervals in each of the Workstation, Employee and External Resource classes. The setup-calendar-default warning and archived missing-actual diagnostic remain visible. Live projections also report missed forecast starts because the fixed horizon begins before the live evaluation time. These are visible warnings, not omitted work.

At 1,000 operations/one client, median live Timeline phase times were 239.1 ms for Run readiness/card assembly, 117.8 ms for the Machine calculation engine and 105.9 ms for auxiliary projection. Phase measurements can overlap and are not additive. Run readiness remains a useful profiling target. The 200-operation Board comparison with/without history has mixed median/tail results; small samples do not establish history-scaling behavior. The larger Board still misses the proposed 500 ms p95 target; D05 remains open.

Reproduce with `./scripts/profile-planner.ps1 -Operations 200,1000 -Samples 5 -Workload shop -HistoryEvents 5000 -Area board` and `-Area timeline`, using distinct output paths. Evidence: `artifacts/d05-shop-board-nohistory.json`, `artifacts/d05-shop-board-history.json`, `artifacts/d05-shop-timeline-history.json`. The original mode was smoke-tested at 16 operations/two rounds over every Timeline horizon in `artifacts/d05-shop-independent-smoke.json`; it is not a performance acceptance sample. Timeline cold-request timing excludes projection-shape inspection in the final profiler.

Two fixture tests passed, covering every resource class, capacity-one non-overlap, all dependency modes, foreign-key integrity and manual assignment/backlog preservation. Adding unrelated retained history leaves current Machine/resource/dependency projections unchanged; historical diagnostic additions are expected. Four final profiling configurations passed. No application behavior or installer changed in this slice. Still unmeasured: diverse approved recipes, many historical Runs and full CNC cycle evidence, concurrent ingestion/import/backup/package writes, 5,000-operation shop maximum load, real signed-in Windows/TV/tablet traffic, WPF rendering, ingestion lag and maximum-load memory. Those remain required before factory acceptance.

## D05 exact-output recipe and evidence batching (2026-10-08)

Run-card projection now hydrates eligible exact-output recipes in 100-output / 200-parameter chunks, then batches their verification/offset enrichment with each output's exact ID. Complete contexts are retained only for the owning connection/read transaction and are keyed by the full internal package context. Split/ambiguous operation bases, missing keys and preload failures retain scalar reads. Scalar and batch paths share recipe SQL/materialization; allocation validation and tooling evaluation remain in the existing Run readiness path. No schema, index, endpoint shape or mutation-authority change.

Controlled before/after workload: Release TestServer, 16 Machines, 1,000 independent legacy operations sharing one recipe, 20 packages, five rounds per 1/3/5-client group. Runs were sequential, without competing builds/tests. These small samples favor recipe reuse and exclude WPF rendering, production history, realistic resource/dependency diversity and factory ingestion/import/backup contention.

| Board clients | Before p50 ms | After p50 ms | Before p95 ms | After p95 ms | After p99 ms |
| --- | --- | --- | --- | --- | --- |
| 1 | 848.5 | 824.4 | 1310.7 | 1222.3 | 1222.3 |
| 3 | 797.5 | 634.7 | 947.1 | 736.1 | 736.1 |
| 5 | 791.2 | 554.8 | 839.5 | 604.4 | 604.8 |

Across five one-client requests, enrichment helper invocations fell from **5,015 to 20**, with aggregate helper time **2,324.2 to 1,792.9 ms**. These are scalar-or-batch helper counts, not SQL statements: each batch invocation executes multiple chunks. Run readiness/card assembly median fell **454.7 to 266.4 ms**. One-client end-to-end median improved only about 3%; the 3/5-client samples improved more. Phase timings overlap and must not be summed, and scalar/batch per-invocation percentiles are not comparable units.

The profiler recorded about 1.66 GB versus 1.59 GB aggregate process allocations across the five one-client requests. Snapshot dictionaries do not establish a maximum-load memory bound; peak package-load acceptance remains open. All observed Board p95 values still exceed the proposed 500 ms goal. No target is revised and D05 is not closed.

Evidence: `artifacts/d05-outputs-before.json` and `artifacts/d05-outputs-after.json`; reproduce with `./scripts/profile-planner.ps1 -Operations 1000 -Samples 5 -Area board` and separate output paths. Queue/live Timeline observations below were not rerun in this slice. Existing MSI 0.1.184 packages predate these source changes. Next work is production-shaped resource/dependency/history and mixed-load profiling, including remaining per-program allocation/tooling/evaluation costs where measurements justify changes.

## D05 verification and offset enrichment batching (2026-10-08)

Measured scalar readiness enrichment, then batched transactional multi-operation reads in 100-context chunks. Two queries per chunk reuse the scalar reader's exact verification binding and offset-package predicates. The execution-evidence materializer preserves all eight SQL values and their action stamp; single-operation, untransactional and exact-output reads retain their scalar path. Multiple eligible session rows trigger scalar fallback. There is no schema/index or API change.

Controlled synthetic workload: 1,000 independent legacy operations sharing one recipe, 16 Machines, 20 packages, Release TestServer, five rounds for each 1/3/5-client group. Before and after profiling ran sequentially without competing builds/tests. This session's baseline was slower than the previous report's baseline, and its five-client median differed substantially from its one-client median; host/runtime variability and these small samples limit generalization. No factory recipe diversity, ingestion, resource/dependency/history load or WPF rendering was measured.

| Board clients | Before p50 ms | After p50 ms | Before p95 ms | After p95 ms | After p99 ms |
| --- | --- | --- | --- | --- | --- |
| 1 | 2438.2 | 904.6 | 2630.2 | 1290.2 | 1290.2 |
| 3 | 2285.3 | 921.2 | 2597.3 | 993.2 | 993.2 |
| 5 | 1288.1 | 864.4 | 2562.8 | 903.9 | 910.0 |

Across five one-client requests, `planner.readiness.enrichment` recorded 20,000 scalar invocations / 8,169.6 ms before and 5,015 scalar-or-batch invocations / 2,343.6 ms after. After batching, these are 5,000 remaining exact-output scalar enrichments plus 15 batch helper invocations. A batch invocation contains multiple SQL chunks; these counts are not SQL statement counts, and scalar and batch helper latency percentiles are not comparable units. Timings overlap encompassing Board/Run/Timeline phases and must not be added to them.

The observed one-client median is below one second, but Board p95 still exceeds the proposed 500 ms target. D05 acceptance remains open. Next work includes remaining exact-output/evaluation costs and realistic recipe/resource/dependency/history and concurrent factory workload measurement. Existing queue and live Timeline measurements below are historical observations, not rerun claims for this slice.

Evidence: `artifacts/d05-evidence-before.json` and `artifacts/d05-evidence-after.json`. Reproduce with `./scripts/profile-planner.ps1 -Operations 1000 -Samples 5 -Area board` and separate output paths. Existing MSI 0.1.184 packages predate these source changes.

## D05 exact-output reference batching (2026-10-08)

The next controlled profile measured support/release/Tool Room reference loaders in exact-output readiness. At 1,000 operations, five one-client Board requests called these helpers 15,045 times, totaling 601.6 ms. Snapshot-scoped preloading reduced this to 60 calls / 55.3 ms aggregate. These are helper invocations, not individual SQL statement counts: each helper may execute multiple 500-ID chunks. Selected/pinned recipe operations supply release keys; covered-but-absent facts remain absent, uncovered keys fall back to existing reads, and connection/transaction ownership checks reject another or completed snapshot.

Same controlled synthetic workload as earlier: 16 Machines, 1,000 independent legacy single-output operations sharing one recipe, 20 packages, Release TestServer, five rounds per 1/3/5-client group. Before/after runs were sequential without competing builds/tests. The shared recipe is favorable for reuse; factory recipe diversity and mixed workload remain unmeasured.

| Board clients | Before p50 ms | After p50 ms | Before p95 ms | After p95 ms | After p99 ms |
| --- | --- | --- | --- | --- | --- |
| 1 | 1445.0 | 1361.6 | 1690.0 | 1813.8 | 1813.8 |
| 3 | 1393.5 | 1259.2 | 1406.3 | 1366.0 | 1366.0 |
| 5 | 1297.1 | 1255.6 | 1544.3 | 1308.0 | 1309.2 |

One-client Board median improved about 6%, while its p95 increased in these small samples. The measured reduction in reference work is clear; these results do not establish uniformly lower end-to-end tails. The helper-duration metric is `planner.readiness.references`. Current additional observations (without controlled same-session before runs):

| Route / operations | Clients | p50 ms | p95 ms | p99 ms |
| --- | --- | --- | --- | --- |
| Queue / 1,000 | 1 | 469.6 | 513.2 | 513.2 |
| Queue / 1,000 | 3 | 355.4 | 450.5 | 450.5 |
| Queue / 1,000 | 5 | 273.3 | 332.4 | 332.4 |
| Queue / 5,000 | 1 | 1397.0 | 1496.6 | 1496.6 |
| Queue / 5,000 | 3 | 1407.0 | 1491.0 | 1491.0 |
| Queue / 5,000 | 5 | 1430.9 | 1457.9 | 1457.9 |
| Live Timeline / 1,000 | 1 | 1013.4 | 1043.0 | 1043.0 |
| Live Timeline / 1,000 | 3 | 1000.6 | 1039.9 | 1039.9 |
| Live Timeline / 1,000 | 5 | 930.9 | 956.4 | 956.5 |

No queue speedup is claimed: the legacy single-output queue commonly uses its existing early-return path, and reference preloading adds bounded work there. Board and larger queues still exceed proposed targets; the live Timeline is near one second in this synthetic workload. Small nearest-rank tail samples and warm explicit-asOf cache timings do not establish factory acceptance. Remaining work includes per-context verification/offset queries and evaluation costs, realistic feasible resource/dependency/history and recipe diversity, actual signed-in Windows rendering, and ingestion during import/backup/package workloads.

Evidence: `artifacts/d05-references-before.json`, `artifacts/d05-references-after.json`, `artifacts/d05-references-queue-after.json`, `artifacts/d05-references-timeline-after.json`. Reproduce with the existing profiler and the same operation counts, five rounds and Board/queue/Timeline area selections. Existing MSI 0.1.184 packages do not include this source follow-up.

## D05 readiness catalog reuse (2026-10-08)

A controlled follow-up instrumented Board source reads and the current verification-enabled package membership query used by manual/setup workflow reporting. Before optimization, five one-client Board reads at 1,000 operations executed this catalog query 20,005 times (2,276.1 ms aggregate, about 455.2 ms per request). Snapshot-local reuse reduces this to 15 queries / 2.9 ms aggregate across the same five requests. Each Board response uses multiple source snapshots; each relevant snapshot loads the catalog once. Exact per-context NC/Offset Loader/session evidence still uses its original queries. Ambiguity detection now groups operation rows once instead of rescanning all rows per operation.

Conditions match the earlier synthetic profile: 16 Machines, 1,000 independent legacy operations, 20 packages, Release TestServer, five rounds per concurrency. Before/after runs were sequential without competing test/build work. New metrics are `planner.board.source` and `planner.readiness.workflow_packages`; phase JSON now includes `totalMs`. All timing evidence below remains descriptive, with small nearest-rank tail samples, no signed-in WPF traffic and no factory contention workload.

| Board clients | Before p50 ms | After p50 ms | Before p95 ms | After p95 ms | After p99 ms |
| --- | --- | --- | --- | --- | --- |
| 1 | 1941.0 | 1355.4 | 2092.1 | 1820.1 | 1820.1 |
| 3 | 1927.9 | 1370.8 | 2037.7 | 1485.3 | 1485.3 |
| 5 | 2040.7 | 1374.1 | 3972.0 | 1412.4 | 1412.4 |

The one-client Board median improved about 30%. These measurements replace earlier observations for the current source tree; historical sections below retain their original evidence. Further runs measured the current queue at 1,000/5,000 operations and current live 30-day Timeline at 1,000 operations. These additional observations have no controlled same-session baseline:

| Route / operations | Clients | p50 ms | p95 ms | p99 ms |
| --- | --- | --- | --- | --- |
| Queue / 1,000 | 1 | 393.5 | 448.6 | 448.6 |
| Queue / 1,000 | 3 | 360.9 | 384.8 | 384.8 |
| Queue / 1,000 | 5 | 278.4 | 286.8 | 286.8 |
| Queue / 5,000 | 1 | 1367.7 | 1441.1 | 1441.1 |
| Queue / 5,000 | 3 | 1351.6 | 1436.9 | 1436.9 |
| Queue / 5,000 | 5 | 1348.3 | 1380.6 | 1380.6 |
| Live Timeline / 1,000 | 1 | 1116.5 | 1201.6 | 1201.6 |
| Live Timeline / 1,000 | 3 | 1106.9 | 1155.9 | 1155.9 |
| Live Timeline / 1,000 | 5 | 1060.2 | 1122.7 | 1122.9 |

The 1,000-operation queue p95 is below 500 ms in these samples. Board, live Timeline and the larger queue still miss the proposed targets; D05 acceptance remains open. The 7/30/180-day explicit-asOf Timeline runs also passed; warm cache times are not calculation-throughput evidence. No feasible resource/dependency/history workload, factory ingestion/backup/import mix, peak permitted package load or actual Windows render acceptance was added in this slice.

Evidence: `artifacts/d05-readiness-before.json`, `artifacts/d05-readiness-after.json`, `artifacts/d05-readiness-queue-after.json`, `artifacts/d05-readiness-timeline-after.json`. Reproduce with the existing profiler, `-Operations 1000 -Samples 5 -Area board` (or `-Area timeline`), and `-Operations 1000,5000 -Samples 5 -Area queue`. The source follow-up is not included in the existing 0.1.184 installers.

## D05 Run-read follow-up (2026-10-08)

Controlled profiling now separates Run graph loading (`planner.runs.graph`) from readiness/card assembly (`planner.runs.readiness`, including shared readiness preloading). The baseline adds only these measurements to the 0.1.184 implementation; the after run uses batched Run graphs and snapshot-local readiness bases. Same Release TestServer workload as below: 1,000 independent legacy operations, 16 Machines, 20 packages, five rounds for each 1/3/5-client group. Runs were sequential without concurrent builds/tests. This remains a synthetic read-path benchmark, with the same factory-load limitations described below.

| Board clients | Before p50 ms | After p50 ms | Before p95 ms | After p95 ms | After p99 ms |
| --- | --- | --- | --- | --- | --- |
| 1 | 2572.4 | 1834.2 | 2615.5 | 2093.9 | 2093.9 |
| 3 | 2445.0 | 1858.8 | 2815.7 | 1976.0 | 1976.0 |
| 5 | 2301.0 | 1924.2 | 4832.1 | 3916.0 | 3916.6 |

At one client, graph loading fell from 55.5 to 7.3 ms median; readiness/card assembly fell from 1,563.5 to 946.4 ms. The aggregate observed SQLite open count across five one-client requests fell from 5,058 to 49 (these include other request/background opens; they are not SQL statement counts). Run lists now use four ordered queries in one read transaction. Card projection reuses this graph and batched operation bases in the same snapshot, with exact-context fallback for ambiguous/split assignments. Start/mutation reads are unchanged, and the combined Board/Timeline response retains its outer revision validation.

| After: live 30-day Timeline clients | p50 ms | p95 ms | p99 ms |
| --- | --- | --- | --- |
| 1 | 1399.0 | 1458.6 | 1458.6 |
| 3 | 1435.7 | 1499.4 | 1499.4 |
| 5 | 1409.0 | 2805.5 | 2806.2 |

The Timeline values above are uncached live reads. The 7/30/180-day explicit-asOf runs also succeeded but their warm cache hits do not measure calculation throughput. No controlled before-Timeline run was made for this follow-up, so these are current observations, not a before/after claim. Five-client tails remain higher than medians; identical requests do not necessarily all arrive within one shared flight. Small nearest-rank sample tails are descriptive only.

Board median improved about 29% at one client, but neither the 500 ms Board p95 nor the one-second refresh target is met here. Remaining work includes exact output-readiness queries, the other Board/Timeline phases, realistic feasible resource/dependency/history datasets, signed-in client rendering and concurrent ingestion/import/backup/package loads. This follow-up does not establish factory acceptance or maximum-load memory bounds.

Evidence: `artifacts/d05-runs-before.json`, `artifacts/d05-runs-after.json`, `artifacts/d05-runs-timeline-after.json`. Reproduce with `./scripts/profile-planner.ps1 -Operations 1000 -Samples 5 -Area board` (or `-Area timeline`) and a distinct output path. Source changes in this follow-up are later than MSI 0.1.184; those installers do not contain them.

## D05 controlled measurements (2026-10-08)

D05 adds a reproducible opt-in profiler: `scripts/profile-planner.ps1`. It uses disposable Server-migrated SQLite databases, never an installed database. Commands and raw outputs are under `artifacts/`; the measured summary is retained here. New code provides queue batching, shared in-flight HTTP reads, bounded explicit-asOf caching, cancellable Timeline/report limits, and package byte/concurrency limits.

### Workload and limits of this evidence

Measured on Windows 10.0.26200, .NET 10.0.11, 20 logical processors, Release binaries with in-process HTTP TestServer. All runs use 16 Machines, 200/1,000/5,000 independent legacy single-output operations, one shared managed process/release, and 20 current production packages. The calendar expands Sunday-Thursday 06:00-18:00 in Asia/Jerusalem across the 180-day window. This is a controlled read-path workload, not a representative factory acceptance dataset: no staffed Employee/Workstation contention, dependency-mode mixture, holiday overrides, independent history growth, actual signed-in WPF/TV/tablet traffic, ERP writes, CNC ingestion, concurrent backup/import/build, or rendered WPF timing. In particular, engine timings from blocked/unqualified work do not establish scheduling throughput for a feasible shop-floor workload.

Each size/route is warmed once, then runs 1/3/5 concurrent in-process clients. The before/after queue comparisons use 5 rounds (5/15/25 observations). Percentiles use nearest rank; p95/p99 with these small sample counts are descriptive, not stable tail-latency estimates. The baseline is the original D04 Server binary copied from the first installer publish, loaded by the same profiling harness; baseline and final queue runs were sequential with no installer build running. Earlier exploratory timings taken while packaging was active are not the comparison below. End-of-group working set includes the test host and accumulated managed memory; it is not peak production Server memory.

### Queue comparison

| Operations | Clients | Before p50 ms | After p50 ms | After p95 ms | After p99 ms | After working set MiB |
| --- | --- | --- | --- | --- | --- | --- |
| 200 | 1 | 203.9 | 103.3 | 132.5 | 132.5 | 172.5 |
| 200 | 3 | 344.4 | 125.2 | 132.0 | 132.0 | 176.1 |
| 200 | 5 | 737.7 | 132.5 | 148.3 | 151.0 | 179.6 |
| 1,000 | 1 | 695.7 | 464.3 | 563.7 | 563.7 | 206.6 |
| 1,000 | 3 | 1320.3 | 407.3 | 433.6 | 433.6 | 221.2 |
| 1,000 | 5 | 3086.8 | 397.7 | 441.6 | 441.6 | 223.3 |
| 5,000 | 1 | not measured | 2159.9 | 2224.2 | 2224.2 | 218.9 |
| 5,000 | 3 | not measured | 2175.6 | 2221.3 | 2221.3 | 229.6 |
| 5,000 | 5 | not measured | 2177.5 | 2229.5 | 2229.6 | 302.6 |

The final queue path batches validity/readiness facts and coalesces identical stage reads while they are in flight. It does not cache completed queue results. The 5,000-operation profile is larger than the measured before-change dataset; no before/after claim is made for that size.

### Board and Timeline observations

| 1,000 operations, one client | p50 ms | p95 ms | Interpretation |
| --- | --- | --- | --- |
| Board | 2223.5 | 2253.4 | Uncached live projection |
| Timeline 7-day | 1.4 | 1.4 | Warm explicit-asOf cache hits; not calculation throughput |
| Timeline 30-day | 1.6 | 1.8 | Warm explicit-asOf cache hits; not calculation throughput |
| Timeline 180-day | 2.0 | 2.2 | Warm explicit-asOf cache hits; not calculation throughput |
| Timeline 30-day live | 1978.7 | 2722.0 | Uncached live projection |

The full-route run used 3 rounds per concurrency. At 1,000 operations the uncached Board/Timeline cost remains around two seconds although the measured Timeline source/engine phases are much smaller. Code inspection identifies per-Run readiness/list reads as the next profiling target; this report does not claim that all remaining time has been attributed. Real session-table revision updates can reduce caching/coalescing effectiveness compared with test authentication.

### Instrumentation and reproduction

Run `./scripts/profile-planner.ps1 -Operations 200,1000,5000 -Samples 5 -Area queue -OutputPath artifacts/planner-profile.json`; use `-Area all` for Board and 7/30/180-day explicit-asOf Timelines plus a live 30-day Timeline. The JSON records workload/runtime, cold/warm timings, p50/p95/p99, response bytes, process allocations, end-of-group working set and phase histograms. The profiling test is skipped unless MEIMAD_PROFILE_OUTPUT is explicitly set. Output paths never select the input database.

The Meter `Meimad.Planner.Performance` records HTTP request duration/count/known response size, SQLite connection-open time/open-busy counts, preparation snapshot time, Timeline source/engine/baseline/auxiliary timings, projection serialization, shared reads and cache hits. It exports no source content, query strings or credentials. Existing per-query Timeline logs and WPF request/apply/render traces are retained. SQLite open-busy counts are not whole-transaction busy-wait instrumentation; external ingestion lag is not measured by this profiler.

### Acceptance still pending

The proposed <500 ms Board/queue p95 and <1 s refresh targets are not achieved across the tested matrix. The 5,000-operation queue remains about 2.2 seconds, and uncached 1,000-operation Board/Timeline reads remain about two seconds. Full production-shaped history/dependency/resource contention, real client render/selection behavior under load, TV/tablets, CNC ingestion lag during import/backup/package builds, maximum-size package memory, write-transaction waits and independent history sizes remain required before factory performance acceptance. D05 remains open for this measurement/optimization work; no pilot target has been silently relaxed.

Artifacts: `artifacts/d05-isolated-baseline.json`, `artifacts/d05-isolated-after.json` (batching before queue coalescing), `artifacts/d05-coalesced-queue.json`, `artifacts/d05-final-profile.json`, and `artifacts/d05-5000-queue.json` (pre-coalescing large queue).

Baseline Server SHA-256: `58977af467fd837b59cde633f6afbb5a29b23809c5255fb1ff1aa0fffb045457`.

Measured D05 Server SHA-256: `363d2e8f9264d79fe4f634b41e4cf948a0fd3aab30cebd10431492abe35c5bf4`.

## Scope

This report covers the read-only Timeline path:

`GET /api/v1/timeline` → SQLite source snapshot → projection and calculation engine → Windows client view-model → WPF canvas.

The instrumentation records elapsed time and input/output cardinalities. It is intentionally observational: it does not change backlog order, scheduling policy, or persisted planning data.

## Instrumentation added

| Area | Signal | Location |
| --- | --- | --- |
| SQLite source snapshot | Total and per-query phase timings; machine, operation, downtime, holiday, and resource counts | `SqliteTimelineSourceRepository` information log |
| Projection and engine | Total request work, source read, primary engine, baseline engine, scheduled-operation, interval, and conflict counts | `TimelineProjectionService` information log |
| Client request/application | API round-trip, view-model application time, projection counts | `System.Diagnostics.Trace` from `TimelineViewModel` |
| WPF canvas | Render time, machine/interval counts, and created visual-element count | `System.Diagnostics.Trace` from `TimelineView` |

Use the server logs to identify database/calculation costs and attach a `TraceListener` (for example Visual Studio Output) to collect client timings. Counts appear alongside every timing so a slow request can be compared fairly with a similarly sized request.

## Identified bottlenecks

1. **Resource exception loading has an N+1 query pattern.** The Timeline source reads active resources, then issues one exception query per resource. This is easy to spot in the `resources and exceptions` timing. It grows linearly in SQL round trips as employees are added.

2. **The projection may run the engine twice.** When a forecast cursor is later than the requested horizon start, it calculates both the current projection and a baseline used for missed-forecast warnings. The new log reports primary and baseline timings separately. The baseline can approximately double calculation CPU for large horizons.

3. **Timeline calculation cost scales with the planning graph.** Dependency traversal, machine/calendar intersections, human-resource reservations, downtime windows, and waiting reasons all increase work per assigned operation. Long horizons with recurring availability windows and dense dependencies should be measured through the engine timing plus scheduled-operation and interval counts.

4. **WPF canvas rendering scales with visual elements.** Every interval, grid line, label, dependency arrow, and arrow head becomes a WPF visual. Large horizons and many short phase/waiting intervals can therefore make rendering and layout dominate API time.

5. **Exact-overlap lane layout previously rescanned all intervals for every interval.** This was an O(n²) per-machine scan. It is now grouped once by start/end before drawing, making this part roughly O(n log n) due to group ordering.

6. **Dependency-arrow lookup previously rescanned every displayed interval for each endpoint.** It now builds an operation endpoint map once per render, avoiding repeated full timeline scans for selected-batch arrows.

## Implemented optimizations

- Employee exceptions are now loaded in one indexed query and grouped by resource in memory. The existing `(resource_id, exception_date, id)` index supports the date-filtered lookup.
- The baseline engine calculation for missed-start warnings now runs only when the Timeline has a later forecast cursor *and* at least one not-started operation. In-progress-only projections no longer pay for a comparison that cannot produce a warning.
- Exact-overlap lane assignment and dependency-arrow endpoints are precomputed once per machine/render instead of repeatedly scanning the same interval lists.

## Concrete optimization steps

1. **Batch employee exceptions in one SQL query.** Fetch exceptions for all active resources with one join (or an `IN` list), group them in memory by resource ID, and retain the existing snapshot transaction. Add an index on `(resource_id, exception_date)` if it is not already present. Do this when `resources and exceptions` is material in production traces.

2. **Avoid the baseline calculation unless the missed-start warning is displayed.** The best long-term design is to persist or retain a non-authoritative prior forecast snapshot and compare it with the current projection. Until then, only calculate the baseline when `forecastCursor > horizonStart`, as the current code does, and monitor its separately logged time.

3. **Bound the calculation horizon in the UI.** The Timeline defaults to a useful but expensive long horizon. Keep the requested range user-controlled and avoid refreshing a broad range repeatedly on every layout change. For very large views, calculate in date chunks only if the dependency boundary data is carried forward correctly.

4. **Virtualize or draw intervals with retained drawing primitives.** If the WPF render trace shows high times or very high visual counts, replace per-interval `Border`/`TextBlock` creation with a custom `OnRender` surface or a virtualized row control. Keep accessible tooltips through hit testing; do not sacrifice operation details merely to reduce visuals.

5. **Coalesce client refreshes.** Planning changes may occur in bursts. Debounce Timeline invalidations for a short interval and cancel stale Timeline HTTP requests, while retaining the existing read-only, server-authoritative refresh behavior.

6. **Use timing thresholds for operational alerts.** Start by investigating any server source, engine, or full projection over 250 ms and any WPF render over 100 ms. Tune these thresholds after collecting production-size traces; they are investigation thresholds, not pass/fail service-level objectives.

## Verification approach

Run deterministic Timeline engine/API tests to protect calculation behavior, then inspect the new logs with a realistic database and horizon. Record:

- requested horizon length;
- machines, assigned operations, dependencies, resources, and intervals;
- source, primary-engine, baseline-engine, total server, API, view-model, and render timings;
- whether a resource-exception N+1 pattern or visual-element count is the dominant contributor.

No production timing figures are claimed in this document. Timings depend on the database size, dependency/resource density, selected horizon, machine hardware, and WPF display environment.
