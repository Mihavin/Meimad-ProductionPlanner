# Windows Planning Client

The implemented .NET 10 WPF application lives under `Meimad.Planner.Client.Windows/`.

It currently provides:

- editable Server root URL with HTTP/HTTPS validation;
- a simple local MVP user display name and stable generated client ID;
- Local AppData persistence for those non-secret settings;
- Server `/health` status and version display;
- server-owned Viewer, Editor, and RequestingEdit indication;
- request, voluntary release, Release/transfer, and Reject actions against the Edit Mode API;
- a five-second status refresh; and
- an explicit offline state that disables editing when Server authority cannot be confirmed;
- an API-backed Case Pool with Part Number/name/customer search, customer filtering, Active/Inactive filtering, and its own always-visible vertical scrollbar independent of the Case-detail tabs;
- preview thumbnails downloaded through the Server API;
- a Case form with its own always-visible vertical scrollbar, editor-only ETag/generation-protected saves, and read-only operation-derived setup/cycle totals;
- independently scrollable STEP Viewer, Operations, Orders, and Batches tabs, including scrollable lists and editor-only creation forms;
- Case Operation create and optimistic edit controls, plus read-only Order and Batch lists with editor-only creation forms;
- total-hours `HH:mm:ss` operation timing input/display while the API continues to exchange integer seconds;
- a required-Machine dropdown built from registered Machine process/axis/capability tokens, with blank Any and legacy-value preservation;
- an Open Working Folder action using the external path returned by the Case API;
- a Machine Planning Board with an unassigned Production Batch Operation pool and Machine backlog columns;
- a bounded local `.stp` / `.step` viewer in the Case workspace, using OpenCascade tessellation of the STEP B-rep faces. Its shared selector provides Shaded (default), Visible edges (shaded faces plus boundary/crease/silhouette edges), and Wireframe (tessellation edges only); the bounding box is off until explicitly enabled. Load performs one camera Fit, while rotation preserves the orthographic camera width and manual wheel zoom until the operator selects Fit again. Solid faces, optional edges, bounding box, and selection overlays share the same model center, camera width, uniform pixel scale, and unmodified model coordinates. Fit uses only the tessellated 3D body, excludes coordinate-system/origin entities, and closed bodies orbit around their signed-volume center of gravity; open geometry falls back to its model-vertex centroid. The viewer supports drag rotation, wheel zoom, standard views, vertex-to-vertex distance/axis-delta measurement in model coordinate units, and PNG snapshot capture into the existing Case picture-path field; no Server data changes until the editor explicitly saves the Case;
- a depth-sorted WPF software surface for the same tessellated STEP triangles, layered with the hardware viewport so a driver-dependent blank `Viewport3D` frame cannot hide a valid loaded solid;
- editor-only Working Calendar creation with timezone, workweek, and shift presets;
- Machine creation with named Working Calendar, process type, and axis type dropdowns instead of typed hard-coded IDs/tokens;
- version-checked Machine editing and guarded Machine deletion;
- a temporary Setup **Excel Case + Order Import** page: choose one `.xlsx` worksheet, preview the authoritative fixed A/B/D/E/F/L/N/O mapping, then import valid Cases and related Orders in one Edit-Mode-gated atomic commit. Invalid rows are reported and skipped. Existing records are matched by Part Number and Case + Order Number without silent updates. The page never submits Batches, Operations, Machines, assignments, backlog, Timeline, or planning data; the former outcome/column/Machine/pattern wizard is bypassed until Kitaron replaces this tool;
- confirmation-protected Case, Case Operation, Order, and Production Batch deletion with Server blocker explanations;
- manual drag-and-drop assignment, stable reorder, cross-Machine move, and drag-back unassignment;
- Start, Suspend, resume, and Finish controls on assigned operation cards, with every transition validated by the Server;
- a conflicts/feedback panel that clearly distinguishes unavailable conflict calculation from server-rejected assignment commands;
- a read-only Timeline with UTC horizon controls and one composite object per operation: `PRODUCTION` is blue (`#1E88E5`), `SETUP` yellow (`#FBC02D`), QA is labeled `QC` and green (`#43A047`), and load/unload is labeled `PART RELOAD` and purple (`#7B1FA2`); locked reservation is orange, internal gaps are transparent, paused hold remains visible, and Server-expanded Machine-calendar closures are gray background columns rather than operation blocks. Generic idle and ordinary anonymous `waiting` capacity bars are suppressed by default so blank row space communicates waiting/idle; assignment-owned `BLOCKED`, paused hold, downtime, actual history, and the conflict panel remain visible, while the API still supplies waiting facts;
- a factory-local two-row hour ruler whose header-only DAY/DARK bands use the Timeline response's `displayTimeZoneId`, `dayStartsAtLocal`, and `dayEndsAtLocal`; they are configured shift-day context, not astronomical daylight or scheduling/calendar capacity, and never add Timeline blocks or change Machine rows;
- one red labelled `NOW` overlay estimated from the Timeline snapshot `readAt` plus elapsed local time, with a factory-timezone label; one shared 30-second throttle refreshes the Server Timeline only while assigned `not_started` forecast or blocked work exists, so embedded and separate windows do not double-poll;
- Server-returned conflict explanations; and
- dependency edges filtered to one selected Production Batch.

It deliberately contains no SQLite provider, database path, planning-domain persistence, automatic scheduling, timeline calculation, or authoritative business-rule implementation. The Excel automatic draft is a reversible edit of the current preview only: it does not call Commit, create Machines or routes, approve cross-type overrides, or update existing records. Duration parsing/formatting and Machine-token option composition are presentation concerns only; the Server validates the resulting seconds/token and owns Case aggregates, route graphs, Batch lifecycle, import atomicity, and Timeline consequences. It never reads preview image files directly: preview bytes come from `/api/v1/cases/{caseId}/preview`. The workbook picker opens the operator-selected stream only for `/imports/legacy-working-plan/preview`; the client does not edit the file or evaluate Excel formulas. Planning-board data comes from `/api/v1/planning-board`; every drop is sent to the assignment API and the client reloads the authoritative result before changing the displayed order. Timeline data comes from `/api/v1/timeline`; the client only filters and renders the Server's calculation output. It derives the display-only `NOW` location from the Server `readAt` snapshot plus elapsed local time, but never computes a schedule or writes forecast state. The time-scale descriptor is additive, so older client versions safely ignore it.

Run from the repository root on Windows:

```powershell
dotnet run --project .\client-windows\Meimad.Planner.Client.Windows\Meimad.Planner.Client.Windows.csproj
```

The settings file is `%LOCALAPPDATA%\Meimad Planner\client-settings.json`. It contains only `serverAddress`, `localUserName`, and `clientId`. The API user ID is a stable ASCII identifier derived locally from the display name so Unicode names remain display-safe; this remains a development-only identity placeholder, not authentication.
