# TV Dashboard

The Meimad Planner Server serves this read-only kiosk UI at `/tv-dashboard/` and its projection at `GET /api/v1/tv-dashboard`.

The dashboard is intentionally dependency-free and requires no separate web host or build step. It displays only display-enabled active Machines and includes top-backlog current/next work, calculated conflicts, urgent Batches, current/upcoming downtime, Server freshness, and automatic conditional refresh. It has no forms, edit controls, Edit Mode calls, or planning mutation code.

For the MVP transition, “current job” means the first unfinished Batch Operation in the stored Machine backlog and “next job” means the following unfinished operation. An urgent Batch serves an active Order whose Work Finish Date is within the configured UTC cutoff. These rules remain server-owned and can evolve without adding logic to the browser.

Open the Server URL in the kiosk browser, for example `http://planner-server:5080/tv-dashboard/`, and configure the browser/operating system for fullscreen kiosk mode. Keep the Server bound only to the approved factory LAN interface.
