# Timeline performance instrumentation and profiling report

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
