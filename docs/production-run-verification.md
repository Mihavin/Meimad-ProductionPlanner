# Production Run verification

Verified on 2026-08-23 for version 0.1.34.

## Automated results

- Server: 520 passed, 0 failed.
- Windows client: 228 passed, 0 failed.
- Fresh/migration schema: v47, foreign-key integrity and repeat startup covered.
- MSI administrative extraction: client 641 files, Server 400 files; both report ProductVersion 0.1.34.

## Acceptance scenarios

| Scenario | Automated evidence |
|---|---|
| A — `2 × A + 1 × B`, targets 20/10 | Pure planner calculates 10 cycles; execution API advances both outputs atomically, completes exactly, treats duplicate identity idempotently, and rejects an extra cycle. |
| B — invalid 20/9 | Pure planner and Server creation validation reject unequal cycle counts without writing allocation rows. |
| C — independent 10/4 programs | Planner proves fixed ordering, completion of the short stream after round 4, skipping thereafter, and continuation of the long stream through cycle 10. |
| D — partial 60/30 allocation | Planner calculates 30 cycles and allocation validation retains 40 units of A as unallocated while planning no additional B. |
| E — migrated assigned operation | Migration tests preserve assignment ID, Machine, backlog position, planning mode, versions/timestamps, execution meaning, pins, release history, and one-program/one-output compatibility. |

The Planning Board and Timeline use compressed arithmetic projections; they do not create one UI or schedule object per physical cycle. CNC observations use an append-only `(source, sourceEventId)` identity and leave quantities unchanged when active-program resolution is unknown or ambiguous.
