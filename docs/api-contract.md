# API Contract

- **Status:** Draft target contract; schema-v17 planning-resource, Setup/Calendar/Machine-Type/Machine-Availability/administrative, Timeline, TV, and E-Ink read/simulator slices are implemented as identified below
- **Transport:** Factory LAN/Wi-Fi only
- **Authority:** Meimad Planner Server

The source specifications define a local REST/API service, domain resources, Single Edit Mode, health, read-only TV data, and five conceptual E-Ink routes. They do not define general endpoint paths, payloads, status codes, identity, or concurrency. This document records the implemented contract where identified and supplies a **Proposed** coherent contract for the remaining surfaces.

### Scope boundary

This contract covers the Windows Planning Client, TV Dashboard, and Server-side read-only E-Ink integration for the MVP. It intentionally defines:

- no Customer Portal routes, customer credentials, or Internet-facing gateway;
- no public/cloud transport or remote-edit contract;
- no native mobile application surface; and
- no ESP32 firmware, device setup-AP/provisioning UI, local checklist synchronization, telemetry, or device write-back.

Firmware remains a separate future project that may consume the frozen E-Ink routes only after simulator and contract tests pass.

## 1. Contract maturity

- `/api/v1` is the proposed versioned base path for all business and device APIs.
- `/health` is implemented as an unversioned liveness endpoint; richer readiness remains proposed.
- Case, Order, Batch creation/read, Machine and Machine Type master data, Working Calendar CRUD and Setup Calendar selection, Machine backlog read, BatchOperation assignment, Single Edit Mode, TV Dashboard, and the E-Ink routes identified below are implemented. Other planning routes remain proposed.
- The source's conceptual `/api/eink/...` paths are implemented as `/api/v1/eink/...`. Future compatibility guarantees/OpenAPI freezing remain TBD.
- JSON field names are `camelCase`. Examples explicitly labeled Implemented describe the tested wire shape; all other examples remain Proposed until an OpenAPI document is approved.
- Human/TV authentication and authorization mechanisms remain TBD. E-Ink device reads use implemented revocable bearer tokens with stored SHA-256 hashes and device/Machine scoping.

Do not implement client and server independently against unreviewed examples. Freeze an OpenAPI document and simulator after Phase 0 decisions, then treat compatibility changes deliberately.

## 2. Caller classes and permissions

| Caller | Required capability | Permitted behavior |
|---|---|---|
| Windows client in View Mode | `planning.read` | Read master data and projections; request Edit Mode. |
| Windows client holding Edit Mode | `planning.read`, `planning.edit` | Read and submit approved planning mutations using the active edit generation. |
| TV Dashboard | `dashboard.read` | Read TV projection only, or an explicitly approved subset. |
| E-Ink device | `eink.read:<deviceId>` | Read only its version, assigned Machine screen, time config, manifest, and authorized package files. |
| Server operator | TBD | Health, backup, restore, migration, and device administration must use a separately approved administrative boundary. |

TV and E-Ink callers must never receive `planning.edit`. A valid user credential without the active Edit Mode generation is insufficient for a planning mutation.

The implemented Windows shell currently uses the development `X-Meimad-Client-Id` and `X-Meimad-User-Id` headers documented below. It persists a stable client ID and local display name, then derives an ASCII API user ID locally; this is not authentication or authorization. Replacing these headers with authenticated identity must preserve the caller-relative Edit Mode states and generation checks.

## 3. Common conventions

### 3.1 Representation

- JSON responses use UTF-8 and `application/json` unless a package asset has its own media type.
- IDs are opaque strings. Clients must not infer type, order, or creation time from an ID.
- Instants use RFC 3339 UTC strings, for example `2026-08-11T11:20:00Z`.
- Local Work Finish Date without time uses ISO `YYYY-MM-DD` until its cutoff/time-zone semantics are decided.
- API durations are non-negative integer seconds and use a `...Seconds` suffix. The Windows client alone formats production-duration inputs and summaries as total-hours `HH:mm:ss`; hours may exceed 23. This display conversion does not change JSON or SQLite representation.
- **Proposed for API v1:** production quantities are positive integers in the Case's implicit production unit. Fractional quantities and explicit units require a later contract decision.
- Enum values normally use stable lower-camel-case contract tokens after their lifecycle is approved; Case Operation dependency types are the explicit uppercase tokens `SEQUENTIAL`, `PARALLEL_CAPABLE`, `INDEPENDENT`, and `LOCKED_SIMULTANEOUS`. Error codes use `snake_case`.
- Resource representations include positive integer `version`, `createdAt`, and `updatedAt` fields. Storage column names never appear on the wire.
- Nullable values are JSON `null`; omitted properties in a PATCH mean “unchanged.”

### 3.2 Request correlation

Clients should send `X-Correlation-Id`; the Server returns it or creates one. Logs and errors use the same value without exposing secrets.

### 3.3 Concurrency

The proposed contract combines:

- `ETag` / `If-Match` for record or aggregate revision.
- `X-Meimad-Edit-Generation` for the current Single Edit Mode generation.

Every planning mutation validates both where applicable. Resource mutations require `If-Match`; commands affecting multiple resources require `expectedPlanRevision` in the body. An absent or stale edit generation returns `409 Conflict` with `edit_mode_required` or `edit_generation_stale`. This contract does not use `423` for MVP.

Example mutation headers:

```http
Authorization: Bearer <credential-format-tbd>
X-Meimad-Client-Id: planner-pc-02
X-Meimad-Edit-Generation: 42
If-Match: "case:opaque-case-id:v7"
Idempotency-Key: 3e65716f-1a1d-4dc7-a777-f48e7cd84040
```

`Idempotency-Key` is the target requirement on resource-creation and transfer-request POSTs so a retry cannot duplicate work. The current Case POST does not persist/deduplicate keys. Single Edit Mode makes a repeated request from the one pending requester idempotent by state, but does not persist general idempotency keys. The retention window and credential binding for keys are TBD. The future authenticated Server derives the user from the credential; caller-supplied identity headers are development-only.

### 3.4 Pagination and filtering

Large collections should use opaque cursor pagination:

```json
{
  "items": [],
  "nextCursor": null
}
```

Exact page limits and filters are TBD. Machine board, timeline, TV, and E-Ink views are projections and should not require clients to join many paginated resource lists.

List responses also include the authoritative revision used:

```json
{
  "items": [],
  "nextCursor": null,
  "planRevision": 123
}
```

The proposed default/maximum page sizes are 50/200. `cursor` is opaque; `limit`, filtering, and ordering must be allow-listed per endpoint.

### 3.5 Success envelopes and status signals

Single-resource reads return the resource directly and an `ETag` header. Creates return `201 Created`, the resource body, `ETag`, and `Location`. PATCH/PUT commands return the updated resource or the command result described below. DELETE assignment commands return `204 No Content` plus the resulting `X-Meimad-Plan-Revision` header.

Any status presented on Windows, TV, or E-Ink includes semantics independent of color:

```json
{
  "code": "inProgress",
  "label": "In progress",
  "icon": "current",
  "color": "#1E88E5"
}
```

The allowed color values and meanings are fixed by the functional specification. Clients may adapt layout, but must preserve `label` or equivalent text and `icon` semantics.

### 3.6 Error envelope

```json
{
  "error": {
    "code": "edit_mode_required",
    "message": "This client does not hold Edit Mode.",
    "correlationId": "opaque-id",
    "details": [
      {
        "field": null,
        "code": "current_editor",
        "message": "Edit Mode is currently held by another client."
      }
    ]
  }
}
```

Messages help users; stable `code` values drive client behavior. Validation errors include field-level details. Errors must not reveal filesystem paths, tokens, SQL, stack traces, or unrelated customer/package data.

### 3.7 Common mutation errors

| Error code | HTTP | Meaning |
|---|---:|---|
| `validation_failed` | `422` | One or more approved domain fields/invariants failed. |
| `precondition_required` | `428` | Required `If-Match`, edit generation, or plan revision was omitted. |
| `resource_version_stale` | `412` | Resource `ETag` no longer matches. |
| `plan_revision_stale` | `409` | A multi-resource command was based on an older plan. |
| `edit_mode_required` | `409` | Caller is not the active editor. |
| `edit_generation_stale` | `409` | Caller previously edited but ownership transferred. |
| `idempotency_conflict` | `409` | A reused key has a different payload. |
| `resource_not_found` | `404` | Resource does not exist or is outside caller scope. |

## 4. Health and service metadata

### `GET /health`

Implemented Server liveness endpoint:

```json
{
  "status": "healthy",
  "service": "Meimad Planner Server",
  "version": "0.1.2",
  "serverTimeUtc": "2026-08-11T11:20:00Z"
}
```

The current endpoint proves only that the process and HTTP pipeline are running. Although SQLite migrations now run during startup, database and migration readiness fields are not currently exposed. It does not expose paths, credentials, host secrets, or exception text. Authentication and a separate detailed readiness endpoint remain TBD.

### `GET /api/v1/service-info`

**Proposed.** Returns API version, supported contract features, server time, and client compatibility range. It does not expose infrastructure secrets.

## 5. Single Edit Mode contract

### 5.1 Read current state

`GET /api/v1/edit-mode` is implemented and requires `X-Meimad-Client-Id`. A future authenticated version also requires `planning.read`. Reading state may atomically materialize an already-expired automatic transfer.

```json
{
  "state": "editor",
  "generation": 42,
  "holder": {
    "clientId": "opaque-client-id",
    "userId": "opaque-user-id",
    "generation": 42,
    "acquiredAt": "2026-08-11T11:00:00Z"
  },
  "pendingRequest": null,
  "serverTime": "2026-08-11T11:20:00Z",
  "transferTimeoutSeconds": 30
}
```

`state` is caller-relative: `viewer`, `editor`, or `requestingEdit`. An unheld token therefore appears as `viewer` with `holder: null`. Conditional polling/notifications remain unimplemented.

### 5.2 Request Edit Mode

`POST /api/v1/edit-mode/requests` is implemented. It requires `X-Meimad-Client-Id` and `X-Meimad-User-Id` and has no request body. These headers are development identity inputs until authentication binds them to a Windows session. TV/E-Ink credential enforcement is pending the authentication layer.

If unheld, the Server grants immediately with `201 Created` and returns the full Edit Mode state. If held, it atomically creates one transfer request and returns `202 Accepted` with the full state, including `pendingRequest`. The default deadline is 30 seconds; `EditMode:TransferTimeoutSeconds` is configurable from 1 through 3600 seconds. Repeating the same pending request is idempotent. Exactly one request may be pending; another requester receives `409 edit_request_pending` and is not queued.

Proposed `202 Accepted` response:

```json
{
  "state": "requestingEdit",
  "generation": 42,
  "holder": {
    "clientId": "planner-pc-02",
    "userId": "planner-user-02",
    "generation": 42,
    "acquiredAt": "2026-08-11T11:00:00Z"
  },
  "pendingRequest": {
    "requestId": "opaque-request-id",
    "requesterClientId": "planner-pc-03",
    "requesterUserId": "planner-user-03",
    "status": "pending",
    "requestedAt": "2026-08-11T11:20:00Z",
    "decisionDeadline": "2026-08-11T11:20:30Z",
    "decidedAt": null,
    "grantedGeneration": null
  },
  "serverTime": "2026-08-11T11:20:00Z",
  "transferTimeoutSeconds": 30
}
```

### 5.3 Read request outcome

`GET /api/v1/edit-mode/requests/{requestId}` is implemented and requires `X-Meimad-Client-Id`. Only the requester or current holder may read it. Reading may first materialize an expired automatic transfer.

```json
{
  "requestId": "opaque-request-id",
  "status": "autoTransferred",
  "requestedAt": "2026-08-11T11:20:00Z",
  "decisionDeadline": "2026-08-11T11:20:30Z",
  "decidedAt": "2026-08-11T11:20:30Z",
  "grantedGeneration": 43
}
```

Implemented statuses are `pending`, `transferred`, `rejected`, and `autoTransferred`. After Reject, the requester returns to Viewer and may submit a new request.

### 5.4 Holder decision

`POST /api/v1/edit-mode/requests/{requestId}/decision` is implemented. It requires the current `X-Meimad-Client-Id` and `X-Meimad-Edit-Generation`.

```json
{
  "decision": "release"
}
```

`decision` is `release` or `reject`. Only the matching current holder generation may decide a pending request. Release transfers immediately and increments the generation. Reject keeps the token and its generation. An identical repeated final decision returns current state; a contradictory one returns `409 edit_request_already_decided`.

### 5.5 Voluntary release

`POST /api/v1/edit-mode/release` is implemented, requires the active client/generation headers, and has no request body. It returns `200` with the resulting state. If a transfer request is pending, voluntary release transfers to that requester and increments the generation. Otherwise it clears the holder and increments the generation, ensuring the released authority is stale. A stale repeat returns `409 edit_generation_stale`.

### 5.6 Automatic transfer

The Server, never a client timer, performs the timeout transition atomically. At the configured deadline with no valid holder response, it invalidates the prior generation and grants a new generation to the requester. A background timeout worker checks every second. Status, Edit Mode commands, and every planning write also process an expired request before checking authority, so the former editor cannot mutate after the deadline even between worker ticks.

The source-compatible default is 30 seconds; this implementation task explicitly approves configuration from 1 through 3600 seconds. Heartbeat, disconnect/crash recovery, unsaved edits, notifications, history retention, authenticated permissions, audit, and takeover UX remain open decisions.

## 6. Domain resource endpoints

Unless explicitly marked implemented, paths in this section are **Proposed**. Mutations require `planning.edit`, active edit generation, server validation, and concurrency preconditions. Current planning repositories validate client ID and generation against the server-owned Edit Token in the same immediate SQLite transaction as the write, but human authentication/capability enforcement is not implemented yet.

### 6.1 Cases and routes

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/cases` | Search/filter Cases, including derived Active state. |
| `POST` | `/api/v1/cases` | Create a Case. |
| `GET` | `/api/v1/cases/{caseId}` | Read Case details. |
| `PATCH` | `/api/v1/cases/{caseId}` | Change approved current master fields. |
| `GET` | `/api/v1/cases/{caseId}/operations` | Read ordered route template and dependencies. |
| `GET` | `/api/v1/cases/{caseId}/preview` | Stream the Case preview image to a Windows planning caller. |
| `POST` | `/api/v1/cases/{caseId}/operations` | Add a Case Operation. |
| `PATCH` | `/api/v1/cases/{caseId}/operations/{operationId}` | Edit one route operation. |
| `POST` | `/api/v1/cases/{caseId}/operations/reorder` | Atomically submit the complete desired route order. |

Case Operation edits do not mutate an existing Production Batch because schema v9 snapshots scalar and dependency fields into Batch Operations. Route reorder remains a separate Proposed command.

**Implemented now:** Case collection GET, POST, GET-by-ID, PATCH, nested operation GET/POST/PATCH, and preview GET. GET-by-ID and collection results include derived `isActive`, based on `active`/`in_production` Order demand or a `waiting`/`in_production` Production Batch. Operation reorder remains Proposed.

The implemented `GET /cases` accepts optional `search`, `customer`, and `isActive` parameters, returns matching records ordered by Part Number, and currently returns `nextCursor: null`. `search` performs case-insensitive substring matching across Part Number, Name, and Customer; `customer` performs case-insensitive Customer substring matching. Customer Reference, status, cursor, and limit filtering remain Proposed.

Implemented Case create request:

```json
{
  "partNumber": "PN-100",
  "name": "Bearing housing",
  "revision": "A",
  "customer": "Customer A",
  "customerReference": "PO-7721",
  "previewPath": "C:\\Cases\\PN-100\\_MeimadPlanner\\preview.png",
  "workingFolderPath": "C:\\Cases\\PN-100",
  "materialType": "Aluminium",
  "materialSpecification": "7075-T6",
  "rawMaterialForm": "Plate",
  "rawMaterialDimensions": "30 x 120 x 180 mm",
  "notes": "Current route values are operation-owned"
}
```

Implemented Case representation:

```json
{
  "caseId": "opaque-case-id",
  "partNumber": "PN-100",
  "name": "Bearing housing",
  "revision": "A",
  "customer": "Customer A",
  "customerReference": "PO-7721",
  "previewPath": "C:\\Cases\\PN-100\\_MeimadPlanner\\preview.png",
  "workingFolderPath": "C:\\Cases\\PN-100",
  "materialType": "Aluminium",
  "materialSpecification": "7075-T6",
  "rawMaterialForm": "Plate",
  "rawMaterialDimensions": "30 x 120 x 180 mm",
  "currentSetupTimeSeconds": 1800,
  "currentCycleTimePerPartSeconds": 240,
  "notes": "Current working values",
  "isActive": true,
  "version": 1,
  "createdAt": "2026-08-11T09:00:00+00:00",
  "updatedAt": "2026-08-11T09:00:00+00:00"
}
```

Part Number, Name, and Working Folder path are required. Both path fields must be absolute filesystem paths; Preview path is optional. The service stores the strings without checking existence, creating directories, reading files, or persisting binary content. Optional PATCH fields may be cleared with JSON `null`; omitting a field preserves it. `currentSetupTimeSeconds` and `currentCycleTimePerPartSeconds` are not accepted in Case POST/PATCH; they are read-only sums of Case Operation timing, with null contributing zero and an empty route returning zero. PATCH requires the exact Case ETag in `If-Match` and returns `412` on a stale version.

The Case timing sums are descriptive route summaries. They do not replace the per-operation durations and dependency semantics used by the Timeline, so clients must not treat either sum as projected elapsed time.

Implemented limits are 200 characters for short identity/material fields, 500 for material specification and raw dimensions, 4,096 for paths, and 8,000 for Notes. Text is trimmed and blank optional text becomes `null`. Case Operation setup/cycle values are nullable non-negative seconds; QA-after-setup and load/unload are non-negative seconds with zero defaults. Case timing fields are derived totals and are not accepted as master-data inputs. Part Number uniqueness remains undecided and is not enforced.

The target contract returns Working Folder and Preview paths only to an authorized Windows planning caller and omits them from TV/E-Ink/errors. The current Case read endpoints have no human authentication layer yet, so deployments must not treat them as production-secure until OD-012 is implemented.

`GET /cases/{caseId}/preview` reads the stored path only inside the Server process and returns PNG, JPEG, BMP, or GIF bytes. It returns `404 preview_not_found` when the path is absent or unavailable and `415 preview_format_unsupported` for other extensions. It does not expose the path in error output. The Windows client uses this route for pool and detail thumbnails and does not open the preview file directly.

Implemented Case Operation create request:

```json
{
  "operationNumber": 20,
  "name": "Finish milling",
  "requiredMachineType": "fiveAxisMill",
  "setupTimeSeconds": 1200,
  "cycleTimePerPartSeconds": 180,
  "qaTimeAfterSetupSeconds": 300,
  "loadUnloadTimeSeconds": 45,
  "loadUnloadRequiresWorker": true,
  "automaticLoading": true,
  "loadUnloadEveryNParts": 5,
  "dayShiftOnly": true,
  "dependencyType": "SEQUENTIAL",
  "predecessorCaseOperationId": "opaque-op-10",
  "simultaneousGroupKey": null
}
```

Implemented Case Operation representation:

```json
{
  "caseOperationId": "opaque-case-operation-id",
  "caseId": "opaque-case-id",
  "operationNumber": 20,
  "routePosition": 1,
  "name": "Finish milling",
  "requiredMachineType": "fiveAxisMill",
  "setupTimeSeconds": 1200,
  "cycleTimePerPartSeconds": 180,
  "qaTimeAfterSetupSeconds": 300,
  "loadUnloadTimeSeconds": 45,
  "loadUnloadRequiresWorker": true,
  "automaticLoading": true,
  "loadUnloadEveryNParts": 5,
  "dayShiftOnly": true,
  "dependencyType": "SEQUENTIAL",
  "predecessorCaseOperationId": "opaque-op-10",
  "simultaneousGroupKey": null,
  "version": 3,
  "createdAt": "2026-08-11T09:10:00Z",
  "updatedAt": "2026-08-11T10:45:00Z"
}
```

POST appends the new operation at the next zero-based route position and validates Edit Mode plus the complete stored Case graph in the insert transaction. Operation Number is positive and unique; Name is required; Machine type is optional; setup/cycle seconds are nullable and non-negative. `INDEPENDENT` forbids a referenced operation; `SEQUENTIAL` and `PARALLEL_CAPABLE` require one; `LOCKED_SIMULTANEOUS` requires both a reference and group key. References must resolve inside the same Case. The one-link persistence shape does not expose arbitrary fan-in/out or reorder. Later Batches copy the expanded route; existing Batch snapshots are never retrofitted.

Implemented Case Operation PATCH is partial. For example:

```json
{
  "name": "Finish milling — revised",
  "requiredMachineType": "fiveAxis",
  "setupTimeSeconds": 900,
  "cycleTimePerPartSeconds": 150
}
```

The accepted fields are `operationNumber`, `name`, `requiredMachineType`, `setupTimeSeconds`, `cycleTimePerPartSeconds`, `qaTimeAfterSetupSeconds`, `loadUnloadTimeSeconds`, `loadUnloadRequiresWorker`, `automaticLoading`, `loadUnloadEveryNParts`, `dayShiftOnly`, `dependencyType`, `predecessorCaseOperationId`, and `simultaneousGroupKey`. JSON `null` clears a nullable field; omission preserves it. `caseOperationId`, `caseId`, and `routePosition` are immutable and an unknown field is rejected. PATCH requires active Edit Mode plus the exact `If-Match: "case-operation:{operationId}:v{version}"`; a stale version returns `412`. The Server merges the partial request, validates the complete Case graph, and increments the operation version in one immediate transaction. Existing Batch Operation scalar and schema-v9 dependency snapshots remain unchanged.

The Windows required-Machine dropdown is populated from the union of `processType`, `axisType`, and `capabilities` returned by the registered Machine catalog. It also offers a blank Any choice and preserves a selected legacy value. This is a client option list, not a new Server enum; the submitted token continues to use the string contract above.

The reorder command contains every current operation exactly once:

```json
{
  "orderedCaseOperationIds": ["opaque-op-10", "opaque-op-20"],
  "expectedCaseVersion": 7
}
```

It changes route order only; it does not silently rewrite dependencies. Missing, duplicate, foreign-Case, or stale operation IDs return `409` or `422` without a partial reorder. Aggregate route revision and arbitrary multi-link dependency persistence remain blocking decisions, so this command is Proposed.

### 6.2 Orders

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/orders` | Search/filter demand. |
| `POST` | `/api/v1/orders` | Create an Order under a Case. |
| `GET` | `/api/v1/orders/{orderId}` | Read Order. |
| `PATCH` | `/api/v1/orders/{orderId}` | Change approved fields/status. |

No endpoint assigns an Order to a Machine.

**Implemented now:** all four routes above. The current list route requires `caseId` and returns all matching Orders ordered by Work Finish Date and Order Number with `nextCursor: null`. The additional `status`, `workFinishFrom`, `workFinishTo`, `search`, `cursor`, and `limit` filters remain Proposed.

Implemented Order create request and representation:

```json
{
  "caseId": "opaque-case-id",
  "orderNumber": "WO-2026-1042",
  "quantity": 50,
  "workFinishDate": "2026-08-20",
  "status": "active",
  "notes": "Customer demand"
}
```

```json
{
  "orderId": "opaque-order-id",
  "caseId": "opaque-case-id",
  "orderNumber": "WO-2026-1042",
  "quantity": 50,
  "workFinishDate": "2026-08-20",
  "status": "active",
  "notes": "Customer demand",
  "version": 2,
  "createdAt": "2026-08-11T09:30:00Z",
  "updatedAt": "2026-08-11T10:30:00Z"
}
```

`orderNumber`, positive integer `quantity`, ISO `YYYY-MM-DD` `workFinishDate`, and `status` are required. Implemented representation tokens are exactly `active`, `in_production`, `complete`, and `cancelled`; `active` and `in_production` contribute to the parent Case's derived `isActive`. Create accepts `active` or legacy/manual `cancelled` only. Manually submitting `in_production` or `complete` for a new or unallocated Order returns `422 order_status_server_owned`. Notes are optional and limited to 8,000 characters. The parent Case is immutable after creation. Create rejects a missing Case, mutation requires the active Edit Mode generation, and PATCH requires the exact Order ETag in `If-Match`.

Once a non-cancelled or explicitly resumed Order has one or more Batch Allocations, its production status is Server-derived across every allocated Batch. It is `complete` only when aggregate allocated quantity is at least the Order quantity, every allocated Batch has at least one Batch Operation, and every operation in every allocated Batch is completed. Otherwise it is `in_production` when any related operation has left `not_started`, and `active` before work starts. Batch creation/deletion and Start, Suspend, and Finish recompute every affected Order in the same transaction as the planning change. This aggregate rule covers split Orders and multi-Order Batches and does not assign an Order directly to a Machine. The legacy already-linked cancellation exception is defined below.

Order PATCH accepts the existing partial fields and is allocation-safe. Reducing `quantity` below the current aggregate allocated quantity returns `409 order_quantity_below_allocated`. For unallocated demand, a manual production token returns `422 order_status_server_owned`. When allocations exist, explicitly sending a `status` inconsistent with the derived result returns `409 order_status_derived`; omitting `status` preserves a valid edit and lets the Server apply the derived value. New Batch creation returns `422` with detail code `cancelled_order` before writing if an allocation references a cancelled Order. A legacy already-linked `cancelled` row remains preserved through automatic recomputation; explicitly PATCHing the status to the value matching current production facts resumes the derived lifecycle. Cross-Batch over-allocation, reallocation, and broader cancellation policy remain open.

`allocatedQuantity` and `remainingQuantity` remain future derived projections because cross-Batch completion/cancellation and over-allocation semantics remain unresolved; the implemented Order response does not claim them yet. There is deliberately no Machine or assignment field on an Order.

### 6.3 Production Batches and allocation

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/batches` | Search/filter Production Batches. |
| `POST` | `/api/v1/batches` | Create Batch, allocation, and route snapshot atomically. |
| `GET` | `/api/v1/batches/{batchId}` | Read Batch, allocation, and operation summary. |
| `PATCH` | `/api/v1/batches/{batchId}` | Change future approved Batch fields; status is Server-owned. |
| `PUT` | `/api/v1/batches/{batchId}/allocations` | Replace the complete allocation atomically after balancing validation. |
| `GET` | `/api/v1/batches/{batchId}/operations` | Read concrete Batch Operations. |

**Implemented now:** POST, GET-by-ID, collection GET by Case, and nested operation GET. PATCH and allocation replacement remain Proposed.

The implemented `GET /batches` requires `caseId`, returns all matching Batches newest first, and currently returns `nextCursor: null`. `orderId`, `status`, `assigned`, `search`, cursor, and limit filtering remain Proposed.

Implemented atomic Batch create request:

```json
{
  "caseId": "opaque-case-id",
  "batchNumber": "B-2026-0087",
  "status": "waiting",
  "plannedQuantity": 53,
  "allocations": [
    {
      "allocationType": "order",
      "orderId": "opaque-order-id",
      "quantity": 50
    },
    {
      "allocationType": "scrapAllowance",
      "orderId": null,
      "quantity": 3
    }
  ]
}
```

Implemented Batch representation:

```json
{
  "batchId": "opaque-batch-id",
  "caseId": "opaque-case-id",
  "batchNumber": "B-2026-0087",
  "status": "waiting",
  "plannedQuantity": 53,
  "routeRevision": null,
  "allocations": [
    {
      "allocationId": "opaque-allocation-id",
      "allocationType": "order",
      "orderId": "opaque-order-id",
      "quantity": 50
    },
    {
      "allocationId": "opaque-scrap-id",
      "allocationType": "scrapAllowance",
      "orderId": null,
      "quantity": 3
    }
  ],
  "batchOperationCount": 3,
  "version": 1,
  "createdAt": "2026-08-11T10:00:00Z",
  "updatedAt": "2026-08-11T10:00:00Z"
}
```

Creating a Batch and its route-derived Batch Operations is one immediate SQLite transaction. Failure creates neither the Batch nor partial allocations/operations. The create contract requires `status: "waiting"`; this is a fixed lifecycle assertion, not a user-selectable status, and any other value is rejected. Later status changes are Server-owned. Every Order allocation must reference an Order under the Batch Case. `plannedQuantity` must exactly equal the sum of `order`, `stock`, and `scrapAllowance` rows. A Batch must include Order demand or stock; scrap alone is rejected. Allocation quantities are positive integers, zero rows are omitted, an Order appears once, and at most one stock and one scrapAllowance row are accepted. These rules support one Order, a partial Order, multiple same-Case Orders, combined stock/scrap, and stock-only creation without making Planner authoritative for warehouse balance.

Batch status tokens are exactly `waiting`, `in_production`, and `complete`. A zero-operation Batch stays `waiting`. For a non-empty Batch, all `not_started` operations mean `waiting`; any `in_progress`, `suspended`, or `completed` operation while another remains unfinished means `in_production`; all `completed` means `complete`. The Server recomputes the derived status in the same transaction as an operation execution transition and advances the Batch version only when the status token changes. Apart from the fixed `waiting` assertion on create, clients display status but never choose or update it.

`PUT /allocations` remains Proposed and would replace the complete collection atomically after future cross-Batch lifecycle rules are approved. It must never update ERP stock.

Implemented Batch Operation representation returned by `/batches/{batchId}/operations`:

```json
{
  "batchOperationId": "opaque-batch-operation-id",
  "batchId": "opaque-batch-id",
  "sourceCaseOperationId": "opaque-case-operation-id",
  "operationNumber": 20,
  "routePosition": 1,
  "name": "Finish milling",
  "requiredMachineType": "fiveAxisMill",
  "setupTimeSeconds": 1200,
  "cycleTimePerPartSeconds": 180,
  "qaTimeAfterSetupSeconds": 300,
  "loadUnloadTimeSeconds": 45,
  "loadUnloadRequiresWorker": true,
  "automaticLoading": true,
  "loadUnloadEveryNParts": 5,
  "dayShiftOnly": true,
  "status": "not_started",
  "version": 1,
  "createdAt": "2026-08-11T10:00:00Z",
  "updatedAt": "2026-08-11T10:00:00Z"
}
```

Creation copies every current Case Operation's identity, route position, name, Machine type, timing, dependency type, predecessor source Case Operation ID, and simultaneous-group key. These persisted fields remain unchanged if the Case Operation later changes. Dependency snapshot fields are currently internal Timeline inputs rather than members of the Batch Operation read representation above. The Timeline resolves the predecessor source ID to the corresponding operation within the same Batch and never rereads the mutable Case Operation relationship. `routeRevision` is currently `null`, and arbitrary multi-record dependency relationships remain OD-005/OD-010 work. No scheduling, Machine assignment, or timeline calculation occurs during creation.

### 6.4 Machines, assignments, and downtime

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/machines` | Read Machine catalog and current display summary. |
| `POST` | `/api/v1/machines` | Create a Machine. |
| `GET` | `/api/v1/machines/{machineId}` | Read Machine/configuration. |
| `PATCH` | `/api/v1/machines/{machineId}` | Change approved Machine fields. |
| `DELETE` | `/api/v1/machines/{machineId}` | Delete an unreferenced Machine. |
| `GET` | `/api/v1/machines/{machineId}/picture` | Stream the optional Machine picture to a Windows planning caller. |
| `GET` | `/api/v1/machines/{machineId}/backlog` | Read ordered Batch Operations and projections. |
| `POST` | `/api/v1/machines/{machineId}/backlog/reorder` | Atomically submit desired backlog order. |
| `GET` | `/api/v1/machine-assignments` | Query assignments by Machine or Batch Operation. |
| `GET` | `/api/v1/machine-assignments/{assignmentId}` | Read one assignment. |
| `PUT` | `/api/v1/batch-operations/{batchOperationId}/assignment` | Assign or move one Batch Operation to a Machine/position. |
| `DELETE` | `/api/v1/batch-operations/{batchOperationId}/assignment` | Unassign one Batch Operation. |
| `GET` | `/api/v1/batch-operations/{batchOperationId}/assignment-overrides` | Read immutable cross-type assignment confirmations. |
| `POST` | `/api/v1/batch-operations/{batchOperationId}/start` | Start or resume the first assigned operation. |
| `POST` | `/api/v1/batch-operations/{batchOperationId}/suspend` | Suspend an in-progress operation with a required structured reason. |
| `POST` | `/api/v1/batch-operations/{batchOperationId}/finish` | Complete an in-progress operation and advance the backlog. |
| `GET` | `/api/v1/downtimes` | Query Machine downtime. |
| `GET` | `/api/v1/downtimes/{downtimeId}` | Read one downtime and ETag. |
| `POST` | `/api/v1/downtimes` | Add planned maintenance or report a breakdown. |
| `PATCH` | `/api/v1/downtimes/{downtimeId}` | Optimistically edit planned maintenance. |
| `POST` | `/api/v1/downtimes/{downtimeId}/restore` | Optimistically close an active breakdown with restored time/repair note. |
| `GET` | `/api/v1/working-calendars` | List Working Calendars for Machine selection. |
| `POST` | `/api/v1/working-calendars` | Create a recurring weekly Working Calendar. |
| `GET` | `/api/v1/working-calendars/{workingCalendarId}` | Read one Working Calendar and its version. |
| `PATCH` | `/api/v1/working-calendars/{workingCalendarId}` | Optimistically update a recurring weekly Working Calendar. |
| `DELETE` | `/api/v1/working-calendars/{workingCalendarId}` | Delete an unreferenced Working Calendar. |
| `GET` | `/api/v1/setup-calendar` | Read the dedicated Setup Calendar selection. |
| `PUT` | `/api/v1/setup-calendar` | Select a Working Calendar for setup availability. |
| `DELETE` | `/api/v1/setup-calendar` | Clear the dedicated selection and use the documented fallback. |
| `GET` | `/api/v1/machine-types` | List reusable Machine Types. |
| `POST` | `/api/v1/machine-types` | Create a reusable Machine Type. |
| `GET` | `/api/v1/machine-types/{machineTypeId}` | Read one Machine Type and its version. |
| `PATCH` | `/api/v1/machine-types/{machineTypeId}` | Optimistically update a Machine Type. |
| `DELETE` | `/api/v1/machine-types/{machineTypeId}` | Delete an unreferenced Machine Type. |

Guarded active-editor deletion is implemented at `DELETE /api/v1/cases/{caseId}`, `DELETE /api/v1/cases/{caseId}/operations/{caseOperationId}`, `DELETE /api/v1/orders/{orderId}`, `DELETE /api/v1/batches/{batchId}`, and `DELETE /api/v1/machines/{machineId}`. Success returns `204`; a missing resource returns `404 resource_not_found`; a protected relationship returns `409 delete_blocked`. Case deletion requires no Orders, Batches, or Operations. Operation deletion requires no dependent Case Operation, instantiated Batch Operation, or remaining locked-simultaneous peer and compacts route positions. Order deletion requires no Batch Allocation. Batch deletion requires no Machine Assignment or official package, then deletes only its allocations and Batch Operations. Machine deletion requires no assignment, downtime, device binding, or official package. These endpoints never delete external folders, images, engineering files, or package bytes.

An accepted assignment may produce warnings/conflicts; it must not cause silent rescheduling. Which structural errors reject versus which planning conflicts are accepted and reported is TBD.

**Implemented now:** Machine collection GET/POST, GET/PATCH/DELETE by ID, guarded Case/Operation/Order/Batch deletes, Machine picture GET, Machine backlog GET, assignment PUT/DELETE, Batch Operation execution POST commands, Machine Type CRUD, Working Calendar CRUD with usage/work/break/dated-exception data, dedicated Setup Calendar read/set/clear, and Machine downtime list/read/create/planned-edit/breakdown-restore. Backlog reorder POST, Machine Assignment query routes, downtime recurrence/cancellation, overnight Calendar windows, automatic holiday linkage, and calendar archive remain Proposed. E-Ink binding administration is implemented separately under `/api/v1/eink/device-registrations`.

Schema-v11-v14 Setup administration adds active-editor CRUD at `/api/v1/resources` and `/api/v1/israeli-holidays`, plus optimistic `GET/PUT /api/v1/report-email-settings`. `POST /api/v1/israeli-holidays/sync` accepts `fromYear`/`toYear`, explicitly fetches Hebcal, atomically updates local provider rows, preserves manual overrides, and returns counts plus attempt/success/error state. Provider failure returns `succeeded: false` without changing cached holidays. Holiday status is `non_working`, `working`, or `partial_working`; partial working requires one same-day local start/end range. Working Calendar create/update includes `useIsraeliHolidays` (default false). A calendar-specific dated exception takes precedence; cached non-working closes the day, working preserves the recurring schedule, and partial-working replaces it with the holiday range. Timeline and employee-availability reads use SQLite only and never require internet. Resource updates use `ETag: "resource:{id}:v{version}"`; holiday updates use `ETag: "israeli-holiday:{id}:v{version}"`; report settings use `ETag: "report-email-settings:1:v{version}"`.

Schema v20 extends report settings with `weeklyMaterialReportEnabled`, lowercase `weeklyMaterialReportSendDay`, and `weeklyMaterialReportTimeLocal` (`HH:mm`). `GET /api/v1/reports/weekly-material-order` returns only `{ items: [{ casePartNumber, requiredMaterialPieceQuantity }] }`. `POST /api/v1/reports/weekly-material-order/send` requires current Edit Mode and sends that same minimal report to configured recipients. Each non-complete Batch serving at least one Order due in the upcoming Sunday-Saturday week is counted once; summing `planned_quantity` includes its explicit scrap allowance. The background sender uses the configured local weekday/time/timezone and records one successful automatic delivery per target week.

Schema v21 adds `weeklyEmployeeEfficiencyEnabled`, `weeklyEmployeeEfficiencySendDay`, and `weeklyEmployeeEfficiencyTimeLocal`. `POST /api/v1/employee-work-measurements` requires current Edit Mode and user identity and records employee/date planned and actual seconds plus optional source reference/notes. `GET /api/v1/reports/weekly-employee-efficiency` returns the previous completed Sunday-Saturday week grouped by employee with planned, actual, signed difference, nullable percentage difference, available calendar capacity, and nullable planned/actual capacity percentages. Capacity respects employee calendars, breaks, cached holidays, and employee exceptions. `POST /api/v1/reports/weekly-employee-efficiency/send` requires Edit Mode; automatic delivery is idempotent per completed week. No payroll, employee ranking, Machine efficiency, or Machine maintenance fields are returned.

Schema v22 adds read-only `GET /api/v1/event-log?from=<RFC3339>&to=<RFC3339>&eventType=<token>&limit=<1..5000>`. Items contain `eventId`, `eventType`, `timestamp`, `user`, structured `relatedEntityIds`, nullable `reasonCode`/`comment`, and nullable `beforeData`/`afterData`. Cross-type override, backlog reorder, Operation execution/pause, and downtime events commit atomically with their source mutation. Timeline conflict and resource-wait detections use `user: system` and stable daily keys so polling does not duplicate identical detections. The endpoint exports evidence only and never runs analytics or mutates the plan.

The implemented Machine collection returns the complete catalog ordered by Machine Number with `nextCursor: null`. Proposed filters are `processType`, `isActive`, `search`, `cursor`, and `limit`.

Implemented Machine create request and representation:

```json
{
  "number": "M-07",
  "name": "Five-axis mill 7",
  "processType": "milling",
  "axisType": "fiveAxis",
  "capabilities": ["fiveAxis", "probe"],
  "workingCalendarId": "opaque-calendar-id",
  "isActive": true,
  "displayEnabled": true,
  "picturePath": "C:\\MachinePictures\\M-07.jpg",
  "machineTypeId": "opaque-machine-type-id"
}
```

```json
{
  "machineId": "opaque-machine-id",
  "number": "M-07",
  "name": "Five-axis mill 7",
  "processType": "milling",
  "axisType": "fiveAxis",
  "capabilities": ["fiveAxis", "probe"],
  "workingCalendarId": "opaque-calendar-id",
  "isActive": true,
  "displayEnabled": true,
  "picturePath": "C:\\MachinePictures\\M-07.jpg",
  "deviceId": "opaque-device-id",
  "backlogCount": 4,
  "version": 3,
  "createdAt": "2026-08-01T08:00:00Z",
  "updatedAt": "2026-08-10T12:00:00Z",
  "machineTypeId": "opaque-machine-type-id"
}
```

`machineTypeId` is an optional stable link to the reusable catalog. Existing schema-v9 Machines are linked during migration from their case-insensitive legacy `processType` values. For compatibility with existing Case Operation requirements, a linked type's name is mirrored into `processType`; Machine-specific `capabilities`, `axisType`, and the linked Machine Type's capabilities all participate in Server assignment validation. Machine and Machine Type changes that would invalidate a current assignment return `409 assigned_operation_incompatible`.

Implemented Machine Type create request and representation:

```json
{
  "name": "Five-axis milling",
  "capabilities": ["milling", "fiveAxis", "probe"]
}
```

```json
{
  "machineTypeId": "opaque-machine-type-id",
  "name": "Five-axis milling",
  "capabilities": ["milling", "fiveAxis", "probe"],
  "version": 1,
  "createdAt": "2026-08-12T08:00:00Z",
  "updatedAt": "2026-08-12T08:00:00Z"
}
```

Machine Type POST requires Edit Mode. PATCH accepts partial `name` and `capabilities`, requires `If-Match: "machine-type:{machineTypeId}:v{version}"`, and returns the updated representation. Names are case-insensitively unique; duplicate names return `409 machine_type_name_conflict`. Renames propagate the compatibility process name to linked Machines in the same transaction, but return `409 machine_type_name_in_use` while a Case Operation or unfinished Batch Operation requires the old name. DELETE returns `409 machine_type_in_use` while a Machine, Case Operation, or Batch Operation references the type; success returns `204`.

Implemented assign/move request:

```json
{
  "machineId": "opaque-machine-id",
  "backlogPosition": 2
}
```

Implemented assignment command response:

```json
{
  "machineAssignmentId": "opaque-assignment-id",
  "batchOperationId": "opaque-batch-operation-id",
  "machineId": "opaque-machine-id",
  "backlogPosition": 2,
  "version": 1,
  "createdAt": "2026-08-11T11:10:00Z",
  "updatedAt": "2026-08-11T11:10:00Z"
}
```

The `PUT` returns `201` for a first assignment or `200` for a move. It changes only the named assignment plus necessary backlog-position normalization as one explicit atomic command and returns the assignment record.

Assignment without an override requires an active Machine and a case-insensitive match between `requiredMachineType` and its process/Machine Type name; a missing requirement accepts any active Machine. Axis and Machine/linked-type capability matches do not suppress the warning when the selected Machine Type differs. A different active Machine Type returns `409 machine_type_override_required` without changing the backlog. To proceed, the caller resubmits the same explicit Machine/position with:

```json
{
  "machineId": "opaque-5-axis-machine-id",
  "backlogPosition": 2,
  "compatibilityOverride": {
    "confirmed": true,
    "reason": "3-axis Machine is unavailable; approved by production lead."
  }
}
```

`confirmed` must be true and trimmed `reason` must contain 1–1,000 characters; otherwise `422 validation_failed` returns `confirmation_required`, `reason_required`, or `too_long`. The Server derives the confirmer from the active Edit Mode token rather than trusting actor values in the body. It atomically stores the assignment plus an immutable audit snapshot containing Operation/Machine IDs, original required type, selected Machine process/type, reason, client ID, user ID, and UTC confirmation time. `GET .../assignment-overrides` returns these entries as `{ "items": [...] }`. Inactive Machines remain non-overridable. The Server never chooses a Machine or position, never changes the Operation route, requested positions outside the target insertion range are rejected atomically, and `409 operation_in_progress` prevents any proposal that would displace a running operation from backlog position zero.

Proposed backlog reorder request:

```json
{
  "orderedBatchOperationIds": [
    "opaque-operation-a",
    "opaque-operation-b",
    "opaque-operation-c"
  ],
  "expectedPlanRevision": 124
}
```

The list must contain every currently assigned operation for that Machine exactly once. The Server rejects omissions, duplicates, foreign-Machine operations, and stale revisions atomically. A successful response returns the full backlog projection and new `planRevision`.

Unassignment removes only the Machine Assignment. It does not cancel the Batch Operation or assign it elsewhere. Current assignment responses contain no timeline/conflict calculation. A future conflict-aware response may report consequences but must never repair them silently.

Start, Suspend, and Finish requests have no body and require the active Edit Mode headers. A successful command returns:

```json
{
  "batchOperationId": "opaque-batch-operation-id",
  "machineId": "opaque-machine-id",
  "status": "in_progress",
  "version": 2
}
```

Start accepts `not_started` or `suspended`, requires an assignment at backlog position zero, and rejects a Machine that already has another `in_progress` operation. Suspend and Finish accept only `in_progress`. Suspend requires `reasonType`: `additional_qa`, `tooling_problem`, `customer_request`, or `other`. Their required fields are respectively `problemDescription`, `toolingItemDescription`, both `customerContactName` and `requestDescription`, or `comment`; an optional comment is retained for every type. Missing data returns `422 validation_failed` without mutation. The Server records `pausedBy` from Edit Mode authority and the start timestamp; Resume atomically closes the event with its end timestamp. An in-progress operation cannot be moved or unassigned; it must be suspended first, otherwise assignment commands return `409 operation_in_progress`. Suspend retains assignment and position. Finish changes status to `completed`, deletes the active assignment, and compacts remaining positions without starting or moving anything else. Every accepted Start/Suspend/Finish transition recomputes the parent Production Batch status in the same transaction; the Batch row and version change only when that derived token changes. Suspended work remains `in_production`, and the final Finish makes a non-empty Batch `complete`. Completed operations are omitted from the active Planning Board and cannot be assigned again.

The implemented Machine master requires an existing Working Calendar. Clients obtain the opaque `workingCalendarId` from `GET /working-calendars`; users are not expected to know or type it. `picturePath` is optional, must be an absolute filesystem path, and is stored as text only. The Server does not require the file to exist during create/update. `GET /machines/{machineId}/picture` returns PNG, JPEG, BMP, or GIF bytes, `404 picture_not_found` for a missing/unavailable path, and `415 picture_format_unsupported` for another extension; errors do not expose the path. `deviceId` is a read-only projection of the optional enabled E-Ink binding administered through the active-editor device-registration API. PATCH requires the Machine ETag and rejects changes that would make assigned operations incompatible. Setting `isActive` false therefore requires an empty backlog. `displayEnabled` does not affect assignment compatibility.

Implemented Working Calendar create request:

```json
{
  "name": "Extended shift",
  "timeZoneId": "Asia/Jerusalem",
  "workdays": ["sunday", "monday", "tuesday", "wednesday", "thursday"],
  "windows": [
    { "startsAtLocal": "06:00", "endsAtLocal": "22:00" }
  ],
  "breakWindows": [
    { "startsAtLocal": "12:00", "endsAtLocal": "12:30" }
  ],
  "exceptions": [
    { "date": "2026-09-13", "windows": [], "breakWindows": [], "name": "Closed" },
    {
      "date": "2026-09-14",
      "windows": [{ "startsAtLocal": "08:00", "endsAtLocal": "16:00" }],
      "breakWindows": [{ "startsAtLocal": "12:00", "endsAtLocal": "12:30" }],
      "name": "Short day"
    }
  ],
  "usages": ["machine", "setup_worker", "regular_worker", "qa_worker"]
}
```

The response adds `workingCalendarId`, `scheduleKind: "weekly"`, normalized start-ordered `windows`, `breakWindows`, date-ordered `exceptions`, normalized `usages`, compatibility single-shift fields (populated only when there is exactly one working window), `version`, `createdAt`, and `updatedAt`. Collection GET returns `{ "items": [...], "nextCursor": null }` ordered by name; item GET returns the resource with `ETag: "working-calendar:{workingCalendarId}:v{version}"`. POST requires active Edit Mode headers, generates the ID on the Server, rejects duplicate names case-insensitively with `409 working_calendar_name_conflict`, and returns validation failures as `422 validation_failed`. Workdays use lowercase Sunday-through-Saturday tokens. Times use local `HH:mm`; `24:00` is accepted only for an end. Working and break windows must be non-overlapping and same-day; every break must be fully contained in one working window. Recurring working windows are required. Each ISO `yyyy-MM-dd` exception replaces that day's recurring schedule: an empty `windows` list closes the day, while non-empty windows define special hours and may have their own contained breaks. Duplicate exception dates, overnight windows, and invalid usage tokens are rejected. Usage tokens are `machine`, `setup_worker`, `regular_worker`, and `qa_worker`; omitted legacy usage data is read as all four. The legacy `shiftStartsAtLocal`/`shiftEndsAtLocal` request pair remains accepted as one window but cannot be mixed with `windows`. The timezone must be available to the Server runtime.

PATCH accepts partial `name`, `timeZoneId`, `workdays`, `windows`, `breakWindows`, `exceptions`, `usages`, `shiftStartsAtLocal`, and `shiftEndsAtLocal`, requires the matching Working Calendar ETag, revalidates the resulting complete schedule, and returns the updated resource. Sending an array replaces that complete collection. Legacy stored calendars with explicit UTC windows can be listed/read with `scheduleKind: "explicit"`; the Windows Setup page treats them as read-only. A direct PATCH that intends to replace one must provide enough fields to form a valid recurring weekly schedule. DELETE returns `409 working_calendar_in_use` while any Machine or Employee Resource references the Calendar or while it is the selected Setup Calendar. Removing a usage while a Machine, selected Setup Calendar, or same-role Employee Resource depends on it returns `409 working_calendar_usage_in_use`. Selecting a Calendar without setup-worker usage returns the same conflict. Machine create/update rejects a Calendar without Machine usage as `422 invalid_working_calendar_usage`. Successful delete returns `204`.

The dedicated Setup Calendar selection is a separate singleton projection:

```json
{
  "workingCalendarId": "opaque-calendar-id"
}
```

`PUT /api/v1/setup-calendar` accepts that request under Edit Mode and returns `{ "workingCalendarId": "opaque-calendar-id", "calendar": { ...working calendar representation... } }`. GET returns the same shape and uses nulls when no dedicated Calendar is selected. DELETE clears the selection and returns `204`. The selected Calendar's timezone and weekly/explicit availability are used by the Timeline setup mapper. Clearing it restores Machine-calendar setup fallback plus the `setup_calendar_defaulted` attention conflict; it does not schedule or move work.

## 7. Planning projections

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/planning-board` | Windows board projection: implemented pool/backlogs; future calculated times/conflicts. |
| `GET` | `/api/v1/timeline` | Deterministically calculate/read the timeline for the current manual plan and requested horizon/filter. |
| `GET` | `/api/v1/conflicts` | Current explained conflicts, optionally filtered by Machine, Case, Batch, or severity. |
| `GET` | `/api/v1/tv-dashboard` | Compact, read-only kiosk projection. |

**Implemented now:** `GET /api/v1/planning-board` returns one SQLite read-transaction snapshot containing all unfinished Batch Operations partitioned into the unassigned `pool` or exactly one Machine `backlog`. Each operation includes Batch/Case display identity, operation number/name, required Machine type, current timing values, status, assignment position, Batch planned quantity, sorted distinct allocated Order Numbers, and nullable estimated seconds. Each Machine includes number/name, process/axis/capabilities, active state, and ordered backlog.

The added operation fields are:

```json
{
  "plannedQuantity": 53,
  "orderReferences": ["WO-2026-1042", "WO-2026-1043"],
  "estimatedTimeSeconds": 10740
}
```

`estimatedTimeSeconds` is setup + QA + aggregate load/unload + (`cycleTimePerPartSeconds x plannedQuantity`) using checked wide arithmetic. Manual load/unload occurs per part; automatic loading contributes zero events without a frequency or `ceil(plannedQuantity / loadUnloadEveryNParts)` events. It is null when setup or cycle input is missing or cannot be represented. It is a compact input-derived estimate for the planning card, not a projected start/finish, persisted schedule, or actual-time record. A stock-only Batch returns an empty `orderReferences` array; the Windows client presents that state explicitly rather than inventing an Order.

The current board response also contains `readAt`, `conflictCalculationStatus`, `conflictCalculationMessage`, `conflicts`, `pool`, and `machines`. Until persistence/API orchestration connects the pure engine, the status is exactly `unavailable`, the message says the engine is not connected to this projection, and `conflicts` is empty. Consumers must not interpret that empty list as a conflict-free plan. Assignment rejection feedback is client presentation of the assignment command error and is not stored as a calculated conflict.

The current schema has no durable `planRevision`, so transitional read projections do not claim one. OD-010/OD-011 must define the revision trigger and full conflict catalog before caching/freshness semantics are added.

### 7.1 Timeline calculation

`GET /api/v1/timeline` is implemented as a read-only route. Authentication and the future `planning.read` policy remain pending. Supported query parameters are:

| Parameter | Required | Meaning |
|---|---|---|
| `from` | Yes | Inclusive RFC 3339 UTC horizon start. |
| `to` | Yes | Exclusive RFC 3339 UTC horizon end. |
Only `from` and `to` are implemented. Invalid/missing instants or a non-positive horizon return `400 invalid_timeline_horizon`. Maximum horizon/filter policy remains TBD.

Implemented response shape:

```json
{
  "readAt": "2026-08-11T11:10:00Z",
  "horizonStart": "2026-08-11T00:00:00Z",
  "horizonEnd": "2026-08-18T00:00:00Z",
  "batches": [
    { "batchId": "batch-1", "batchNumber": "B-1", "partNumber": "PN-1" }
  ],
  "machines": [
    {
      "machineId": "machine-1",
      "number": "M-1",
      "name": "Mill One",
      "intervals": [
        {
          "type": "setup",
          "machineId": "machine-1",
          "operationId": "operation-1",
          "batchId": "batch-1",
          "batchNumber": "B-1",
          "partNumber": "PN-1",
          "operationNumber": 10,
          "operationName": "Rough milling",
          "startsAt": "2026-08-11T08:00:00Z",
          "endsAt": "2026-08-11T08:20:00Z",
          "detail": null
        }
      ]
    }
  ],
  "dependencies": [],
  "conflicts": []
}
```

Interval `type` is `setup`, `production`, `waiting`, `idle`, `reserved`, or `downtime`. A `waiting` interval is Machine-available time held by a Sequential predecessor constraint; its detail explains the latest blocking predecessor (for example, `Waiting for OP10 on Machine M01 to finish.`). Operation metadata, including `operationName`, is null for Machine-only intervals. The Windows client uses operation number/name to render a visible marker even when a duration bar is too narrow for its full label. Dependencies include Batch identity, type token, from/to operation identity/number/name, and optional simultaneous-group key. Conflicts include code, severity, explanation, and affected operation/Machine IDs. Unassigned operations, `dependency_predecessor_unassigned`, missing/invalid timing or Machine calendars are returned as explained conflicts rather than silently omitted as a conflict-free plan.

Timeline calculation is read-only with respect to authoritative planning inputs and never changes Machine assignment, backlog position, dependency, quantity, duration input, or Work Finish Date. Sequential edges constrain the child earliest start to calculated predecessor finish plus applicable availability; they may insert waiting gaps but never alter manual backlog order. Locked-simultaneous members have common calculated start/finish; shorter members emit `reserved` intervals through group finish.

The embedded and separate-window Windows Timeline surfaces consume this same GET response through one shared read-only view model. Opening or closing the separate window adds no endpoint and grants no mutation authority.

Each active Machine's Working Calendar is expanded in its timezone with breaks, exceptions, and cached-holiday policy. The selected Setup Calendar is an additional setup constraint. The Timeline also expands every active employee's assigned Calendar and exceptions, then reserves one eligible employee per worker phase. Setup eligibility requires an exact case-insensitive employee skill match against the Machine number, name, type, axis, or effective capability; `*` explicitly matches any Machine. QA and worker-required load/unload use QA and regular-worker roles. A resource already reserved by another calculated phase is unavailable. Among simultaneously ready contenders, the Batch with the earliest allocated-Order Work Finish Date is calculated first; equal dates use the naturally smaller Order Number, followed by stable operation identity only for an exact tie. Contention inserts `waiting` intervals whose `detail` identifies whether due date or Order Number granted the blocking operation priority. Calculated employee choice and priority are not persisted, and stored Machine backlog order is unchanged. Dependencies remain immutable Batch snapshots. Plan revisions/cache/freshness, skill qualification expiry, and persisted worker assignment remain open.

### 7.2 Conflicts

`GET /api/v1/conflicts` requires `planning.read` or `dashboard.read`. Supported filters are `planRevision`, `machineId`, `caseId`, `batchId`, `batchOperationId`, `severity`, `code`, `cursor`, and `limit`. A requested plan revision that is no longer retained returns `409 plan_revision_unavailable` rather than substituting current results.

Example conflict shape:

```json
{
  "conflictId": "stable-within-plan-revision",
  "code": "sequential_overlap",
  "severity": "blocking",
  "title": "Sequential operations overlap",
  "message": "OP20 begins before required OP10 finishes.",
  "interval": {
    "startsAt": "2026-08-11T12:00:00Z",
    "endsAt": "2026-08-11T12:30:00Z"
  },
  "affected": {
    "caseId": "opaque-case-id",
    "batchId": "B026",
    "batchOperationIds": ["opaque-op-10", "opaque-op-20"],
    "machineIds": ["opaque-machine-id"]
  },
  "statusSignal": {
    "label": "Blocking conflict",
    "icon": "warning",
    "color": "#C62828"
  },
  "planRevision": 124,
  "calculatedAt": "2026-08-11T11:10:00Z"
}
```

The list response wraps conflicts with `planRevision`, `calculatedAt`, `freshness`, `items`, and `nextCursor`. Conflict IDs need only remain stable within one plan revision. There is no conflict acknowledgement, dismissal, repair, or “optimize” endpoint in MVP. A planner resolves a conflict only through explicit domain mutations. The conflict catalog, severity policy, and accepted-warning versus rejected-command boundary remain OD-011 decisions.

### 7.3 TV Dashboard read-only projection

`GET /api/v1/tv-dashboard` is implemented, supports `If-None-Match`, and never requires Edit Mode headers. An unchanged projection returns `304` with an empty body. Authentication is not implemented yet; the target policy remains `dashboard.read` or `planning.read`. There are currently no query parameters or display groups.

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-08-11T11:10:00Z",
  "freshness": "current",
  "refreshAfterSeconds": 15,
  "summary": {
    "machineCount": 4,
    "criticalConflictCount": 1,
    "urgentBatchCount": 2,
    "downtimeMachineCount": 1
  },
  "urgentBatches": [
    {
      "batchId": "batch-1",
      "batchNumber": "B-2026-0087",
      "partNumber": "PN-100",
      "workFinishDate": "2026-08-12",
      "isOverdue": false,
      "machineNumber": "M-07"
    }
  ],
  "machines": [
    {
      "machineId": "opaque-machine-id",
      "number": "M-07",
      "name": "Five-axis mill 7",
      "processType": "fiveAxisMill",
      "status": {
        "code": "inProgress",
        "label": "In progress",
        "icon": "current",
        "color": "#1E88E5"
      },
      "current": {
        "operationId": "operation-1",
        "batchId": "batch-1",
        "partNumber": "PN-100",
        "batchNumber": "B-2026-0087",
        "operationNumber": 20,
        "operationName": "Finish milling",
        "status": "not_started",
        "projectedFinish": "2026-08-11T15:20:00Z",
        "urgent": true,
        "workFinishDate": "2026-08-12"
      },
      "next": {
        "operationId": "operation-2",
        "batchId": "batch-2",
        "partNumber": "PN-220",
        "batchNumber": "B-2026-0088",
        "operationNumber": 10,
        "operationName": "Deburr",
        "status": "not_started",
        "projectedFinish": null,
        "urgent": false,
        "workFinishDate": null
      },
      "downtime": null,
      "conflicts": [
        {
          "conflictId": "missing_timing:operation-2:machine-1",
          "code": "missing_timing",
          "severity": "blocking",
          "message": "Batch B-2026-0088 OP10 is missing setup or cycle timing."
        }
      ]
    }
  ]
}
```

Only active Machines with `display_enabled = true` appear. Until an execution lifecycle exists, `current` is the first unfinished stored backlog item and `next` is the second. Urgent Batches serve an active Order whose `workFinishDate` falls within `TvDashboard:UrgentWithinHours` using the current UTC-date baseline; the default is 48 hours. `refreshAfterSeconds` defaults to 15. `projectedFinish` comes from the Server Timeline projection when calculable. The dashboard projection contains only shop-floor display data and omits Working Folder paths, package links, customer details, credentials, edit authority, and mutation links.

The implemented route is GET-only; POST/PUT/PATCH/DELETE do not match it. Credential-class enforcement and `403` behavior remain pending the authentication layer. The web client calls no Edit Mode or mutation route, sends conditional ETags, and preserves its last rendered snapshot when a refresh fails. Offline-display telemetry and plan revisions remain unimplemented.

## 7.6 Official job package generation

`POST /api/v1/job-packages` is implemented for an active Windows editor. It is an official publication command, not a tablet endpoint. It requires `X-Meimad-Client-Id` and `X-Meimad-Edit-Generation`; the Server checks authority before reading source files and again in the publication transaction.

```json
{
  "batchOperationId": "operation-opaque-id",
  "revision": "R3",
  "toolCartId": "TC-12",
  "includePreview": true,
  "files": [
    {
      "assetType": "nc",
      "sourceRelativePath": "NC/OP20_MAIN.nc",
      "logicalPath": "nc/OP20_MAIN.nc"
    },
    {
      "assetType": "text",
      "sourceRelativePath": "SETUP/fixture.txt",
      "logicalPath": "text/fixture.txt"
    }
  ],
  "toolTable": [
    {
      "toolId": "T01",
      "description": "End mill",
      "diameter": "10 mm",
      "length": "75 mm",
      "note": "Prepared"
    }
  ],
  "expectedMachineTools": [
    { "toolId": "T99", "description": "Probe", "note": "Expected loaded" }
  ],
  "localChecklistItems": [
    { "itemId": "tools-collected", "label": "Tools collected from Tool Room" },
    { "itemId": "machine-verified", "label": "Tools on Machine verified" }
  ],
  "offsets": [
    { "name": "G54 Z", "value": "-125.40", "unit": "mm", "note": "Fixture top" }
  ],
  "instructions": "Verify fixture and dry-run the first cycle."
}
```

The Batch Operation must exist and have a current Machine Assignment. The Server snapshots Machine ID/number/name, Case ID/part number/name/revision/customer, Batch ID/number/planned quantity, and Batch Operation ID/number/name. Schema v19 also snapshots the Timeline-calculated setup start/finish, selected active setup worker ID/first/last name, optional packaged photo reference, official job tools, optional expected-on-Machine tools, and local checklist seed items. Missing optional data remains null/empty for compatibility. `revision` is unique per Batch Operation; a correction publishes a different revision rather than changing an existing package.

Source files are read-only inputs. `sourceRelativePath` must stay under the Case Working Folder. `nc` accepts the configured baseline NC extensions; `text` accepts the baseline text formats. A requested preview must be the Case preview inside that Working Folder. Tool table, offsets, and instructions are package-specific official inputs, not a full tool inventory and not device-local annotations. The Server generates JSON/text assets for them.

Generation stages files under the configured Server-local package root, enforces configured per-file/total/count/text limits, calculates SHA-256, and moves the staged directory into its final opaque package ID before publishing SQLite metadata. Publication revalidates Edit Mode and the Case/Batch/Operation/assignment/Machine versions atomically. A failure removes the unpublished staged/final directory. SQLite stores paths and checksums only, never file bytes.

The `201` response contains `packageId`, `revision`, `toolCartId`, `publishedAt`, the immutable `snapshot`, and `assets`. Each asset contains `fileId`, `assetType`, safe `logicalPath`, media type, byte length, SHA-256, and display order; it never exposes a source or storage path. There is no package PATCH/PUT/DELETE route.

## 8. E-Ink read-only contract

All E-Ink routes require an implemented revocable `mp_eink_...` bearer credential whose subject matches `{deviceId}`. Only the token's SHA-256 hash is stored. The Server resolves the assigned Machine/package from registration; a device cannot select an arbitrary Machine, package, or file by changing an ID.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/eink/devices/{deviceId}/version` | Small conditional change check used first on wake. |
| `GET` | `/api/v1/eink/devices/{deviceId}/machine-screen` | Assigned Machine/current/next read-only projection. |
| `GET` | `/api/v1/eink/devices/{deviceId}/package-manifest` | Resolve current authorized package manifest. |
| `GET` | `/api/v1/eink/devices/{deviceId}/packages/{packageId}/revisions/{revision}/manifest` | Read an exact authorized revision manifest. |
| `GET` | `/api/v1/eink/devices/{deviceId}/packages/{packageId}/revisions/{revision}/files/{fileId}` | Download one authorized manifest file or preview. |
| `GET` | `/api/v1/eink/devices/{deviceId}/time-config` | Read workday, shift-window, and polling configuration. |

Every route is GET-only. E-Ink credentials receive `403` on planning, Edit Mode, TV, device-registration administration, and any future telemetry route not explicitly scoped to that credential. A path/credential device mismatch returns scoped `404` so it does not reveal another device's registration.

### 8.0 Registration and binding administration

These implemented Server administration routes are for the Windows planning/operator surface, not the tablet:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/v1/eink/device-registrations` | List registration metadata; never returns credential hashes or an existing token. |
| `POST` | `/api/v1/eink/device-registrations` | Create a named spare or Machine-bound E-Ink device; returns the new token once. |
| `PATCH` | `/api/v1/eink/device-registrations/{deviceId}` | Bind/unbind, enable/revoke, and optionally rotate the credential. |

POST/PATCH require the same active `X-Meimad-Client-Id`, `X-Meimad-User-Id`, and `X-Meimad-Edit-Generation` authority as planning mutations; the authority check and registration write share one immediate SQLite transaction. Create accepts `{ "deviceName": "M07 tablet", "machineId": "machine-7" }`. PATCH accepts `{ "machineId": "machine-7", "isEnabled": true, "rotateCredential": false }`. `machineId` may be null for a spare. The plaintext `registrationToken` is present only in a successful create or rotation response. Human authentication and a narrower administrator role remain OD-012.

### 8.1 Version/change check

`GET /api/v1/eink/devices/{deviceId}/version`

This should be the first, small request in a normal wake cycle. It supports `If-None-Match`; unchanged state should return `304 Not Modified` with no body.

Implemented response when changed:

```json
{
  "schemaVersion": 1,
  "deviceId": "device-opaque-id",
  "machineId": "machine-opaque-id",
  "machineScreenRevision": "screen-revision",
  "package": {
    "packageId": "package-opaque-id",
    "revision": "2026-08-11.03"
  },
  "timeConfigRevision": "time-config-revision"
}
```

The response's ETag represents only the device assignment, screen revision, package revision, and time-configuration revision. Volatile clock time is excluded. The standard HTTP `Date` header supplies server time, including on a `304` response where supported by the Server stack. Revision values are opaque implementation hashes and must not be parsed.

If the registered spare is unassigned, the response remains `200` and uses `machineId: null`, `machineScreenRevision` for an explicit unassigned screen, and `package: null`. Missing, invalid, revoked, or disabled credentials return scoped `404`, never an unassigned response.

### 8.2 Machine screen

`GET /api/v1/eink/devices/{deviceId}/machine-screen`

The implemented v1 form is structured `application/json`, rendered by the simulator and intended for a later firmware consumer. A pre-rendered panel asset is not implemented and would require an explicit compatible media/profile contract.

A structured projection contains Machine ID/type, last server revision, current part/Batch/Operation/quantity/status, next work, status text/icon/color token, and package linkage. Status always has text/symbol semantics independent of color.

Implemented structured response shape:

```json
{
  "schemaVersion": 1,
  "deviceId": "device-opaque-id",
  "machineScreenRevision": "screen-revision",
  "generatedAt": "2026-08-11T11:10:00Z",
  "machine": {
    "machineId": "machine-opaque-id",
    "number": "M-07",
    "name": "Five-axis mill 7",
    "processType": "fiveAxisMill"
  },
  "status": {
    "code": "current",
    "label": "Current job",
    "icon": "current",
    "color": "#1E88E5"
  },
  "current": {
    "partNumber": "PN-100",
    "batchNumber": "B-2026-0087",
    "batchOperationId": "opaque-batch-operation-id",
    "operationNumber": 20,
    "operationName": "Finish milling",
    "quantity": 53,
    "status": "not_started",
    "projectedFinish": "2026-08-11T15:20:00Z"
  },
  "next": [
    {
      "partNumber": "PN-220",
      "batchNumber": "B-2026-0088",
      "operationNumber": 10,
      "operationName": "Rough milling",
      "quantity": 25,
      "status": "not_started",
      "projectedFinish": null
    }
  ],
  "conflicts": [],
  "package": {
    "packageId": "package-opaque-id",
    "revision": "2026-08-11.03",
    "manifestPath": "/api/v1/eink/devices/device-opaque-id/packages/package-opaque-id/revisions/2026-08-11.03/manifest"
  }
}
```

`next` contains at most three entries. An idle/unavailable/unassigned response uses explicit status text/icon and `current: null`; it never fabricates work. `conflicts` contains the Server Timeline conflicts scoped to the assigned Machine. Preview assets, when published, are ordinary authorized manifest files downloaded through the revision-qualified file route.

### 8.3 Package manifest

`GET /api/v1/eink/devices/{deviceId}/package-manifest`

This convenience route resolves the device's currently authorized package revision. Its response includes `Content-Location` for the exact revision-qualified manifest:

`GET /api/v1/eink/devices/{deviceId}/packages/{packageId}/revisions/{revision}/manifest`

Implemented response shape:

```json
{
  "schemaVersion": 1,
  "packageId": "package-opaque-id",
  "revision": "2026-08-11.03",
  "machineId": "machine-opaque-id",
  "batchId": "batch-opaque-id",
  "batchOperationId": "operation-opaque-id",
  "toolCartId": "TC-12",
  "publishedAt": "2026-08-11T10:55:00Z",
  "metadata": {
    "machine": { "machineId": "machine-opaque-id", "number": "M-07", "name": "Five-axis mill 7" },
    "part": { "caseId": "case-opaque-id", "partNumber": "PN-100", "name": "Bracket", "revision": "C", "customer": "Customer" },
    "batch": { "batchId": "batch-opaque-id", "batchNumber": "B-2026-0087", "plannedQuantity": 53 },
    "operation": { "batchOperationId": "operation-opaque-id", "operationNumber": 20, "name": "Finish milling" },
    "setup": { "worker": { "resourceId": "employee-id", "firstName": "Miriam", "lastName": "Cohen", "photoFileId": "photo-file-id", "photoDownloadPath": "/api/v1/eink/devices/device-opaque-id/packages/package-opaque-id/revisions/2026-08-11.03/files/photo-file-id" }, "plannedStartsAt": "2026-08-11T11:00:00Z", "plannedEndsAt": "2026-08-11T11:30:00Z" },
    "tools": { "job": [{ "toolId": "T01", "description": "End mill" }], "expectedOnMachine": [{ "toolId": "T99", "description": "Probe" }] },
    "localChecklist": { "storage": "device_sd", "syncToServer": false, "commentsSupported": true, "items": [{ "itemId": "tools-collected", "label": "Tools collected from Tool Room" }] },
    "tabletPolicy": { "transport": "wifi", "persistentStorage": "sd", "serverAccess": "read_only", "reverseSynchronization": false, "usbMassStorage": false }
  },
  "files": [
    {
      "fileId": "file-opaque-id",
      "assetType": "nc",
      "logicalPath": "nc/OP20_MAIN.nc",
      "downloadPath": "/api/v1/eink/devices/device-opaque-id/packages/package-opaque-id/revisions/2026-08-11.03/files/file-opaque-id",
      "mediaType": "text/plain; charset=utf-8",
      "byteLength": 12345,
      "modifiedAt": "2026-08-11T10:54:00Z",
      "checksum": {
        "algorithm": "sha-256",
        "value": "lowercase-hex"
      }
    }
  ]
}
```

`logicalPath` is normalized and non-traversing. `downloadPath` binds every download to the same package and revision as the manifest. The Server authorizes every file independently and never exposes its storage-relative or full filesystem path. SHA-256, byte length, media type, and modified time are implemented; package size limits, allowed file types/encoding, signatures, and retention remain TBD.

If no package is assigned, the convenience manifest route returns `404 package_not_assigned`. A malformed or unpublished revision is never partially exposed. The exact revision-qualified route returns `404` when it is not authorized for that device, regardless of whether it exists elsewhere.

### 8.4 Package file

Source shorthand: `GET /api/eink/devices/{device_id}/package-file/{file_id}`.

Implemented revision-safe route:

`GET /api/v1/eink/devices/{deviceId}/packages/{packageId}/revisions/{revision}/files/{fileId}`

- Returns the authorized bytes with declared `Content-Type`/length, content-specific `ETag`, `X-Meimad-Checksum-SHA256`, and private immutable cache metadata. Before returning bytes, the Server verifies the disk file's configured-root containment, length, and SHA-256 against schema v7 metadata.
- Rejects a file not present in the device's authorized manifest, even if the file ID exists.
- A client must use only revision-qualified links from one manifest. It must never mix file links across manifests.
- If reassignment, revocation, or publication policy makes that exact revision unavailable during download, the Server rejects the request. The tablet aborts staging, retains last-known-good content, and performs a new version check. Any grace period for finishing an older authorized revision is TBD under OD-023/OD-026.
- Range/resume support is TBD and should follow measured package sizes and ESP32 memory/storage behavior.
- A missing current assignment should not reveal whether another Machine's file exists.

### 8.5 Time configuration

`GET /api/v1/eink/devices/{deviceId}/time-config`

The implemented response contains a configured time-zone identifier, workdays, one shift window, poll interval, retry limits, an opaque configuration revision, and an empty dated-exceptions list. The clock/NTP/RTC strategy, cross-platform zone mapping, multiple windows, and dated exceptions must be chosen with firmware support.

```json
{
  "schemaVersion": 1,
  "revision": "time-config-7",
  "timeZoneId": "Asia/Jerusalem",
  "workdays": ["sunday", "monday", "tuesday", "wednesday", "thursday"],
  "shiftWindows": [
    {
      "startsAtLocal": "06:00",
      "endsAtLocal": "18:00"
    }
  ],
  "pollIntervalSeconds": 300,
  "retry": {
    "maximumAttempts": 3,
    "initialBackoffSeconds": 15
  },
  "datedExceptions": []
}
```

Automatic checks are permitted only inside these workday/shift windows. The contract does not prevent a physical manual Refresh from waking and checking at any time.

### 8.6 No checklist/comment endpoint

There is intentionally no route to upload tool checks, local statuses, or comments. The manifest supplies checklist seed data and declares device-SD storage with `syncToServer: false`; completed marks and comments remain on the tablet.

### 8.7 Telemetry decision

The source's optional `GET .../ping?battery=...&fw=...` is not part of the baseline because it creates a side effect and conflicts with the stated read-only boundary.

If telemetry is explicitly approved later, use a separately scoped, rate-limited command such as `POST /api/v1/eink/devices/{deviceId}/telemetry`, keep its fields bounded, and guarantee that it cannot mutate planning state. Treat last-seen/battery/firmware as operational records only.

### 8.8 E-Ink error behavior

| Error code | HTTP | Device behavior |
|---|---:|---|
| `device_resource_not_found` | `404` | Missing, invalid, revoked, or mismatched device credential/resource; do not infer registration state. |
| `package_not_assigned` | `404` | Retain current verified package but do not activate it as current work. |
| `service_unavailable` | `503` | Retain last-known-good content and follow bounded backoff. |

An exact revision that is no longer the current authorized package returns scoped `404`; the consumer must abort staging and perform a fresh version request. Dedicated rate limiting and a `package_revision_changed` error are not implemented.

Missing/corrupt SD and local checksum failures are device-side states, not upload APIs. The simulator must prove that these failures never activate partial content.

## 9. Caching and consistency

- Server mutation commits atomically and produces one new plan revision.
- Read projections identify the plan revision used.
- Schema v7 and `POST /job-packages` treat published snapshot/file metadata as immutable. Corrections create a new revision; approval roles, retention, and superseded-revision access still require OD-022/OD-023 decisions.
- Device manifests and files use stable ETags/checksums.
- A tablet stages, verifies, and atomically activates a complete package; partial content never becomes active.
- On error, the tablet retains last-known-good content and reports stale/offline state locally.
- Cache-control must not allow a revoked device to fetch new content; revocation semantics for already cached files are a physical-risk decision.

## 10. Status-code baseline

| Status | Meaning in this API |
|---|---|
| `200` | Successful read or mutation with body. |
| `201` | Resource created. |
| `202` | Edit request or other accepted asynchronous transition. |
| `204` | Successful command with no body. |
| `304` | Conditional E-Ink/read projection unchanged. |
| `400` | Malformed request or contract violation. |
| `401` | Missing/invalid credential. |
| `403` | Authenticated caller lacks required capability/scope. |
| `404` | Resource unavailable in the caller's scope. |
| `409` | Domain state conflict, edit-generation conflict, or atomic command conflict. |
| `412` | `If-Match` revision is stale. |
| `415` | Request media type is unsupported. |
| `422` | Structurally valid payload violates approved domain validation. |
| `428` | A required precondition header or expected plan revision is missing. |
| `429` | Rate limit exceeded. |
| `500` | Unexpected server failure with safe error envelope. |
| `503` | Server not ready, for example migration/database unavailable. |

## 11. Security requirements

- Bind only to approved factory interfaces.
- Do not rely on LAN location as authentication.
- Use least-privilege caller capabilities.
- Verify Edit Mode and concurrency server-side for every mutation.
- Keep TV and E-Ink credentials read-only and separate from human sessions.
- Scope each device to its assigned Machine/package and support revocation.
- Never return SQLite paths, network credentials, raw stack traces, or unrelated customer data. Return a Case Working Folder path only to an authorized Windows planning caller; omit it from TV, E-Ink, errors, and logs.
- Redact authorization headers and tokens from logs.
- Define request/body/file limits before accepting package or text content.
- Prevent path traversal and content-type confusion for Working Folder/package files.

TLS/certificate deployment, identity provider, login, token storage/rotation, CSRF/browser strategy, CORS, device enrollment, audit, and operator APIs are TBD.

## 12. Compatibility and contract tests

- Publish one OpenAPI source of truth after decisions are approved.
- Generate or validate clients against that contract without making generated DTOs the domain model.
- Use consumer contract tests for Windows, TV, simulator, and firmware.
- Preserve additive compatibility within `/api/v1`; removals or semantic changes require a new version or an explicit coordinated migration.
- Include schema/version fields in long-lived E-Ink JSON and manifest formats.
- Maintain simulator fixtures for unchanged, changed, offline, revoked, missing SD, corrupt manifest, checksum mismatch, interrupted download, and new-revision behavior.
- Test that read-only credentials cannot call every mutation path, not just the UI-hidden ones.

## 13. MVP route coverage and implementation gate

| Required surface | Contract routes | Access |
|---|---|---|
| Cases | `/cases`, `/cases/{caseId}`, nested `/operations` and `/reorder` | Windows read; active editor mutates. |
| Orders | `/orders`, `/orders/{orderId}` | Windows read; active editor mutates; never Machine-assigned. |
| Production Batches | `/batches`, `/batches/{batchId}`, `/allocations`, nested `/operations` | Windows read; active editor mutates atomically. |
| Machines | `/machines`, `/machines/{machineId}`, nested `/backlog` and `/reorder` | Windows read; active editor mutates. |
| Machine Types | `/machine-types`, `/machine-types/{machineTypeId}` | Windows read; active editor creates/optimistically updates/deletes when unreferenced. |
| Working/Setup Calendars | `/working-calendars`, `/working-calendars/{workingCalendarId}`, `/setup-calendar` | Windows read; active editor manages weekly calendars and the dedicated setup selection. |
| Machine Assignments | `/machine-assignments`, `/batch-operations/{id}/assignment` | Windows read; active editor explicitly assigns/moves/unassigns. |
| Timeline calculation | `/timeline` | Read-only deterministic projection; never repairs the plan. |
| Conflicts | `/conflicts` | Read-only explained projection; no dismiss/repair route. |
| Single Edit Mode | `/edit-mode`, `/edit-mode/requests`, request outcome/decision, `/edit-mode/release` | Implemented development identity headers; Windows-only credential policy pending auth. |
| TV Dashboard | UI `/tv-dashboard/`; projection `/api/v1/tv-dashboard` | Implemented read-only TV UI and projection; auth pending. |
| Official job packages | `POST /job-packages` | Implemented active-editor immutable generation/publication; no update/delete. |
| E-Ink | `/eink/devices/{deviceId}/version`, `/machine-screen`, `/package-manifest`, revision manifest/files, `/time-config`; admin `/eink/device-registrations` | Implemented device-scoped GET-only data; active-editor create/update registration. |

Case and Case Operation create/read/update, Order create/read/allocation-safe update/derived production lifecycle, Batch creation/read/derived lifecycle, Machine and Machine Type master data, recurring multi-window/break/dated-exception Working Calendar CRUD and dedicated Setup Calendar selection, Machine backlog, explicit Batch Operation assignment/execution, Timeline calculation, TV Dashboard, Single Edit Mode, official job-package generation, E-Ink device administration/read APIs, and E-Ink simulator described above are implemented. Before implementing the remaining endpoints, convert this Markdown contract into reviewed OpenAPI and approve identity, Calendar overnight/archive/automatic-holiday policy, aggregate route revision/reorder, arbitrary dependency fan-in/out, cross-Batch over-allocation/reallocation, final Timeline rules, conflict policy, Edit Mode recovery/notification/audit behavior, and E-Ink package approval/retention. Structural changes after that point require a deliberate versioning decision.

The contract cannot be frozen until the API, identity, edit-token, data-model, timeline, rendering, package, and telemetry questions in [Implementation plan](implementation-plan.md#open-decisions) are resolved.
