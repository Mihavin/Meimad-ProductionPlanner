# TV Dashboard

The Meimad Planner Server serves this read-only kiosk UI at `/tv-dashboard/` and its projection at `GET /api/v1/tv-dashboard`.

The dashboard is dependency-free and requires no separate web host or build step. Its no-scroll compact dark board displays one horizontal band per display-enabled active Machine: number/name and connection dot, Part Number, Batch/Operation, Operation name, large textual execution state, calculated completion, and a thin progress bar. Idle bands explicitly show `No current operation`. Setup displays a percentage; production displays the current part against Batch quantity and overall Batch percentage. Pauses freeze progress at the pause time. It deliberately does not render part previews, conflicts, warnings, next/third jobs, host text, summaries, configuration, forms, edit controls, Edit Mode calls, or planning mutation code.

The richer dashboard projection remains Server-owned and continues to derive Machine status from authoritative planning state. The kiosk deliberately renders only the status result; it does not calculate or mutate planning state in the browser.

Open the Server URL in the kiosk browser, for example `http://planner-server:5080/tv-dashboard/`, and configure the browser/operating system for fullscreen kiosk mode. Keep the Server bound only to the approved factory LAN interface.

The Timeline tab remains embedded in the main window and offers a separate-window action. Both surfaces use the same read-only Timeline view model and committed Server projection. Opening, refreshing, or closing the separate window cannot assign, reorder, start, suspend, finish, or otherwise mutate planning data.
