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
- a Case form with editor-only ETag/generation-protected saves;
- read-only Operations, Orders, and Batches tabs;
- an Open Working Folder action using the external path returned by the Case API;
- a Machine Planning Board with an unassigned Production Batch Operation pool and Machine backlog columns;
- editor-only Working Calendar creation with timezone, workweek, and shift presets;
- Machine creation with named Working Calendar, process type, and axis type dropdowns instead of typed hard-coded IDs/tokens;
- version-checked Machine editing and guarded Machine deletion;
- confirmation-protected Case, Case Operation, Order, and Production Batch deletion with Server blocker explanations;
- manual drag-and-drop assignment, stable reorder, cross-Machine move, and drag-back unassignment;
- Start, Suspend, resume, and Finish controls on assigned operation cards, with every transition validated by the Server;
- a conflicts/feedback panel that clearly distinguishes unavailable conflict calculation from server-rejected assignment commands;
- a read-only Timeline with UTC horizon controls and labeled setup, production, idle, downtime, and reserved intervals;
- Server-returned conflict explanations; and
- dependency edges filtered to one selected Production Batch.

It deliberately contains no SQLite provider, database path, planning-domain persistence, automatic scheduling, timeline calculation, or business-rule implementation. It never reads preview image files directly: preview bytes come from `/api/v1/cases/{caseId}/preview`. Planning-board data comes from `/api/v1/planning-board`; every drop is sent to the assignment API and the client reloads the authoritative result before changing the displayed order. Timeline data comes from `/api/v1/timeline`; the client only filters and renders the Server's calculation output.

Run from the repository root on Windows:

```powershell
dotnet run --project .\client-windows\Meimad.Planner.Client.Windows\Meimad.Planner.Client.Windows.csproj
```

The settings file is `%LOCALAPPDATA%\Meimad Planner\client-settings.json`. It contains only `serverAddress`, `localUserName`, and `clientId`. The API user ID is a stable ASCII identifier derived locally from the display name so Unicode names remain display-safe; this remains a development-only identity placeholder, not authentication.
