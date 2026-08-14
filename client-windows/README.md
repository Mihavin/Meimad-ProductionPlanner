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
- an API-backed Case Pool with Part Number/name/customer search, customer filtering, and Active/Inactive filtering;
- preview thumbnails downloaded through the Server API;
- a Case form with editor-only ETag/generation-protected saves and read-only operation-derived setup/cycle totals;
- Case Operation create and optimistic edit controls, plus read-only Order and Batch lists with editor-only creation forms;
- total-hours `HH:mm:ss` operation timing input/display while the API continues to exchange integer seconds;
- a required-Machine dropdown built from registered Machine process/axis/capability tokens, with blank Any and legacy-value preservation;
- an Open Working Folder action using the external path returned by the Case API;
- a Machine Planning Board with an unassigned Production Batch Operation pool and Machine backlog columns;
- editor-only Working Calendar creation with timezone, workweek, and shift presets;
- Machine creation with named Working Calendar, process type, and axis type dropdowns instead of typed hard-coded IDs/tokens;
- version-checked Machine editing and guarded Machine deletion;
- confirmation-protected Case, Case Operation, Order, and Production Batch deletion with Server blocker explanations;
- manual drag-and-drop assignment, stable reorder, cross-Machine move, and drag-back unassignment;
- Start, Suspend, resume, and Finish controls on assigned operation cards, with every transition validated by the Server;
- a conflicts/feedback panel that clearly distinguishes unavailable conflict calculation from server-rejected assignment commands;
- a read-only Timeline with UTC horizon controls and one composite object per operation: `PRODUCTION` is blue (`#1E88E5`), `SETUP` yellow (`#FBC02D`), QA is labeled `QC` and green (`#43A047`), and load/unload is labeled `PART RELOAD` and purple (`#7B1FA2`); locked reservation is orange, internal gaps are transparent, paused hold remains visible, and Server-expanded Machine-calendar closures are gray background columns rather than operation blocks. Generic idle and ordinary anonymous `waiting` capacity bars are suppressed by default so blank row space communicates waiting/idle; assignment-owned `BLOCKED`, paused hold, downtime, actual history, and the conflict panel remain visible, while the API still supplies waiting facts;
- a factory-local two-row hour ruler whose header-only DAY/DARK bands use the Timeline response's `displayTimeZoneId`, `dayStartsAtLocal`, and `dayEndsAtLocal`; they are configured shift-day context, not astronomical daylight or scheduling/calendar capacity, and never add Timeline blocks or change Machine rows;
- one red labelled `NOW` overlay estimated from the Timeline snapshot `readAt` plus elapsed local time, with a factory-timezone label; one shared 30-second throttle refreshes the Server Timeline only while assigned `not_started` forecast or blocked work exists, so embedded and separate windows do not double-poll;
- Server-returned conflict explanations; and
- dependency edges filtered to one selected Production Batch.

It deliberately contains no SQLite provider, database path, planning-domain persistence, automatic scheduling, timeline calculation, or authoritative business-rule implementation. Duration parsing/formatting and Machine-token option composition are presentation concerns only; the Server validates the resulting seconds/token and owns Case aggregates, route graphs, Batch lifecycle, and Timeline consequences. It never reads preview image files directly: preview bytes come from `/api/v1/cases/{caseId}/preview`. Planning-board data comes from `/api/v1/planning-board`; every drop is sent to the assignment API and the client reloads the authoritative result before changing the displayed order. Timeline data comes from `/api/v1/timeline`; the client only filters and renders the Server's calculation output. It derives the display-only `NOW` location from the Server `readAt` snapshot plus elapsed local time, but never computes a schedule or writes forecast state. The time-scale descriptor is additive, so older client versions safely ignore it.

Run from the repository root on Windows:

```powershell
dotnet run --project .\client-windows\Meimad.Planner.Client.Windows\Meimad.Planner.Client.Windows.csproj
```

The settings file is `%LOCALAPPDATA%\Meimad Planner\client-settings.json`. It contains only `serverAddress`, `localUserName`, and `clientId`. The API user ID is a stable ASCII identifier derived locally from the display name so Unicode names remain display-safe; this remains a development-only identity placeholder, not authentication.
