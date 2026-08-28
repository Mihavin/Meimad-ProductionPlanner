# Meimad Production Planner - Repository Rules

This file applies to the entire repository.

## Product intent

Meimad Production Planner is a factory-local, client-server planning system for CNC production. It keeps planning decisions manual while centralizing data, timeline calculation, and conflict explanation.

The repository contains implemented Server, Windows client, TV, E-Ink, migration, and test vertical slices. Do not describe a component, endpoint, migration, test, or device behavior as implemented unless it exists and has been verified in this repository.

## Requirements authority

- Use `docs/functional-spec.md` as the integrated MVP product baseline.
- Use `docs/esp32-eink-work-tablet.md` for tablet-specific hardware, firmware, power, storage, and interaction requirements.
- Use `docs/architecture.md`, `docs/data-model.md`, and `docs/api-contract.md` as target designs, not evidence of implementation.
- Treat statements labeled **Proposed** or **TBD** as decisions awaiting confirmation.
- If documents conflict or leave a material choice open, do not silently choose. Record the issue in `docs/implementation-plan.md` and keep the implementation reversible until it is decided.
- Keep documentation aligned when a decision changes. A change to behavior, storage, or an endpoint must update the relevant specification, model, contract, plan, and tests in the same change.

## Permanent product rules

1. The central Meimad Planner Server is the sole authority for production-planning data.
2. Only the server may open, migrate, back up, or restore the SQLite database. Never let a client open SQLite directly or place the database on a network share.
3. Keep planning manual. Never auto-schedule, auto-optimize, silently reorder, or silently repair a plan. Calculate consequences, detect conflicts, and explain them so a planner can decide.
4. Windows Planning Clients are the only MVP editing clients. Enforce exactly one server-controlled editor at a time through Single Edit Mode. A requester asks the current editor; Release transfers immediately, Reject keeps the current holder, no response transfers automatically after the server-configured timeout (30-second default), and the holder may release voluntarily.
5. TV Dashboard is a read-only operational consumer. E-Ink clients are read-only for planning/package content except for the narrowly scoped `SEND_TO_QC` operational command defined in the API contract. Tablets never request planning Edit Mode or general mutation rights.
6. Keep the MVP on the factory LAN/Wi-Fi. Do not add public Internet exposure, router port forwarding, remote editing, or customer access.
7. Any later customer access must be a separate, minimal, read-only portal. It must not expose drawings, certificates of conformity, or VPN access to the factory network.
8. Preserve the domain separation:
   - A **Case** is the permanent part master and has no production quantity by itself.
   - An **Order** is demand under a Case and is never assigned directly to a Machine.
   - A **Production Batch** is an actual production launch.
   - A **Batch Operation** is the concrete route and quantity obligation for one Production Batch.
   - A **Manufacturing Program** is a reusable approved recipe; its immutable revision declares one or more Case Operation outputs.
   - A **Production Run** is one concrete physical Machine session and becomes the schedulable backlog unit. Existing single-operation work is represented by a one-program, one-output run.
9. Make every batch allocation explicit across selected Orders, stock quantity, and scrap allowance. Do not make Meimad Planner authoritative for warehouse inventory; ERP remains authoritative for stock.
10. Preserve dependency meanings:
    - **Sequential:** the dependent operation occurs after its required predecessor and cannot overlap it.
    - **Parallel-capable:** operations may overlap but may also run sequentially by planner choice.
    - **Independent:** there is no timing or ordering relationship.
    - **Locked simultaneous:** linked operations have the same start and finish; group duration is the longest member duration, and shorter machines remain reserved until group end.
11. The database stores only a Case Working Folder path. Never modify original engineering files. Generated previews or cache data may be written only below that folder's `_MeimadPlanner` directory.
12. MVP timing represents current working setup and cycle values. Do not invent plan-versus-actual history without an explicit scope decision.
13. Color is never the only status signal. Pair every status color with text and/or an icon, and retain the palette defined in `docs/functional-spec.md`.
14. Use one unified Color E-Ink Work Tablet concept, normally one device per Machine plus one or two spares. Use one configurable firmware build; do not compile Machine identity or network settings into custom firmware.
15. Official E-Ink planning/package data flows server-to-device only. Tablets may download assigned package revisions and may send only the authenticated `SEND_TO_QC` operational event for the Server-resolved active Production Run. That command changes only the tablet workflow projection to `IN_QC`; it may not edit planning assignments, backlog order, quantities, allocations, execution counts, or package data. Official package files remain read-only in the device UI.
16. Tablet checklist marks and comments remain device-local, non-authoritative, unsynchronized, and stored separately from official package content. Make an official revision change conspicuous before offering any local-mark cleanup.
17. Do not add USB Mass Storage, CNC write capability, or official CNC-program-carrier responsibility to the tablet in MVP.
18. Restrict each tablet to assigned read-only resources plus its explicit `SEND_TO_QC` event scope with a revocable device credential. The Server, never the tablet, resolves the target Machine and active Production Run. Limit cached confidential data, support lost-device revocation, and keep device health and tablet workflow events separate from planning data.
19. Deep sleep is the normal tablet state. Automatic checks run only during configured workdays and shift windows; manual Refresh wakes the device at any time. Make a small version/change request before a package download or panel refresh; if unchanged, return to sleep without refreshing the E-Ink display.
20. Preserve last-known-good tablet content during outages. Verify manifests/files, reject corrupt or checksum-mismatched downloads, and never activate a partial revision. A missing or corrupt SD card must show an error and prevent package download.
21. The MVP tablet uses three replaceable AA batteries with no rechargeable cell or charger. Prefer on-screen indication and do not add always-on LEDs.
22. Keep the Color E-Ink UI flat and readable: white background, black primary text, large status areas, no gradients, shadows, animation, or tiny color-only labels.
23. Provision a tablet through a temporary setup access point entered by a first-boot/setup gesture. Store configuration locally, restart into normal mode, and provide a long-press or service reset without requiring a Machine-specific firmware build.
24. Keep ESP32 hardware and firmware as a separate project boundary, started only after the E-Ink server API is stable.
25. Allow a development Server executable/console host, but target a Windows Service on a designated factory PC or local Server for production.
26. Do not implement deferred features - automatic planning, ERP synchronization, native mobile apps, full Android-tablet behavior, full tool inventory, tablet write-back other than the approved `SEND_TO_QC` command, Customer Portal, official CNC transfer, rechargeable-device charging, or OTA firmware update - without an explicit scope decision.
27. For multi-output work, keep coupled outputs atomic per NC program cycle, forbid rounding and overproduction, and make run structure immutable after its first program starts. Follow the accepted and implemented decisions in `docs/production-run-architecture.md`; schemas v45–v47 own Manufacturing Programs, Production Runs, and idempotent cycle observations.
28. A persistent CNC Setup/Production macro variable is not a workflow authority and must not be reintroduced. Server workflow is projected from immutable Production Run operational events. Protected temporary CNC variables are permitted only for the separately commissioned setup-verification handshake; their Machine-specific mapping must be configured, not embedded in business rules.
29. Every newly approved NC release must already contain exactly one stable generic Meimad verification hook as its first executable block. The hook carries a globally unique six-digit NC identity bound immutably to that exact release. The Server validates and records it but never inserts or rewrites NC content. Historical releases are not backfilled or treated as verification-eligible until explicitly re-released with a valid hook.

CNC controller-state boundary:

- **Persistent CNC workflow mode variable: REMOVED.**
- **Protected temporary setup verification variables: SUPPORTED**, only for the configured, separately commissioned challenge/response handshake. They are never planning or workflow authority.

## Engineering boundaries

- Put server-owned domain rules, validation, timeline calculation, conflict detection, edit-token coordination, persistence, backup, and API behavior under `server/`.
- Keep `client-windows/` dependent on the documented server API. Do not duplicate authoritative business rules in the client.
- Keep `client-tv-dashboard/` read-only and suitable for fullscreen/kiosk operation.
- Treat the E-Ink API and simulator as server/API work; keep device firmware outside these client directories unless the repository scope is explicitly expanded. The repository contains implemented vertical slices, so keep implementation-status claims synchronized with tests and migrations.
- Implement schema evolution through ordered, server-owned migrations. Never edit an installed database by hand as a deployment step.
- Make backup useful by testing restore and integrity, not merely by copying a database file.
- Keep secrets, device tokens, local database files, logs, build output, and generated package/cache content out of version control.
- Use stable IDs in contracts. Use locale-independent timestamps, durations, quantities, and decimal serialization; document the chosen formats before implementation.
- Make mutations atomic and validate domain invariants on the server even if a client also validates them.
- Keep device telemetry, if approved, narrowly scoped and unable to mutate planning data. Keep `SEND_TO_QC` separately scoped and unable to change anything beyond the tablet workflow projection for the resolved active run.

## Quality and change rules

- Add tests with functionality. At minimum, cover domain invariants, all four dependency modes, timeline/conflict behavior, allocation validation, Single Edit Mode races and timeout behavior, API authorization/read-only boundaries, migrations, backup/restore, and E-Ink cache/checksum failure modes.
- Test that the planner reports conflicts without changing the user's assignments or backlog order.
- Test that TV credentials cannot use mutation endpoints; E-Ink credentials can use only assigned reads and `SEND_TO_QC`, cannot call any other mutation, and cannot target another device/Machine/run. Tablet-local notes never enter server state.
- Preserve accessibility: status must remain understandable without color and on muted Color E-Ink panels.
- Prefer small, reviewable changes. Do not combine an unresolved architecture choice with broad feature implementation.
- Do not claim acceptance from unit tests alone when a requirement depends on Windows Service behavior, LAN deployment, kiosk display, physical E-Ink readability, or measured battery current.
- Validate tablet current draw, refresh/download cycles, provisioning, local persistence, shop-floor readability, and a one-week one-Machine pilot before ordering multiple devices.
- When a task is limited to documentation or scaffolding, do not add application functionality, dependencies, generated binaries, sample databases, or placeholder code that appears executable.
