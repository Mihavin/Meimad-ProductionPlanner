# Kitaron to Meimad initial mapping analysis

Date: 2026-08-17  
Status: Draft for planner/DBA completion; no synchronization was enabled

The editable mapping is [kitaron-initial-mapping.yaml](kitaron-initial-mapping.yaml).

## Scope and safety

The analysis used SQL Server read intent and metadata or bounded `SELECT` queries only. It issued no `INSERT`, `UPDATE`, `DELETE`, DDL, stored-procedure execution, or Kitaron transaction. The Meimad database was compared through its read-only Server APIs and repository schema. No production-planning record was changed.

No password or connection secret is present in either output file.

## Source access result

The requested database `KitaronData2550OLAP` cannot be opened by the configured SQL login, so `dbo.VProductionPlanning` could not be inspected. The same read-only login can inspect:

- `KitaronData229`: 1,251 tables, 3,102 views, and 120,427 catalogued columns.
- `KitaronData229OLAP`: 12 tables and 135 columns. Its accessible objects are Excel-chart/report metadata and document metadata, not a production-planning fact/view set.

`dbo.VProductionPlanning` was not found in either accessible database. Before implementation, the DBA should confirm whether `2550` is the correct company database/version and grant this login read access to the intended view. The initial mapping therefore uses the closest accessible production projection, `KitaronData229.dbo.VQWorkPlanningForStationF4`, and verifies its fields against canonical operational tables.

## Relevant Kitaron model discovered

| Business concept | Canonical Kitaron object | Stable source identity | Important fields |
|---|---|---|---|
| Item master | `TDetails` | `DetailID` / `DetailNumber` | `DetailName`, `REV`, folder/picture/material metadata |
| Customer | `TCUSTOMER` | `CustomerID` | `CompanyName` |
| Sales-order header | `TOrder` | `OrderID` | `OrderNumber`, `CustomerID` |
| Sales-order line | `TSubOrder` | `RecordID` | `DetailID`, `Number` (quantity), `SupplyDate`, `Row` |
| Production work order | `TRootCard` | `NUMBER` | `DetailID`, `ProductionAmount`, `SupplyDate`, status fields |
| Reusable item route | `TDirection` | `DirectionID` | `DetailID`, `ActionNumber`, `OperationID`, `StationID` |
| Operation master | `TOperation` | `OperationID` | operation label/default timing candidates |
| Work-order operation | `TSubRootCard` | `NUMBER + ActionNumber` | operation description, route link, station, execution fields |
| Station/work center | `TStation` | `StationID` | station name/type and capacity metadata |
| Raw-material purchase line | `TBuyRow` + `TBuyMain` | `TBuyRow.BuyRowID` | purchase order/line, `RowMaterialID`, description, ordered quantity, requested delivery, status |
| Supplier delivery approval | `TAppCostOfferBySupplier` | latest `AppCostOfferID` for purchase order/row/supplier | `AppDate`, acknowledged `Amount`, `Remark`, `PresentDate` |
| Material receipt history | `TBuyReceptionHeader` | `BuyReceptionID` | purchase order/row, historical received quantity, closed state |

The raw-material purchase source was revalidated on 2026-08-22 against live bounded reads. `TBuyRow` contains current material purchase lines, while the generic `Q*BuyStringUnion` report views are session/filter dependent and returned no rows in a direct connector session. The connector therefore uses a deterministic base-table join and selects the latest supplier-approval row by `PresentDate DESC, AppCostOfferID DESC`. These imported receipt and approval facts remain advisory and never become locally verified material availability.

The accessible active projection contains 312 rows representing 135 production work orders, 127 items, 37 sales orders, 312 unique work-order operation keys, and 18 stations. None of these rows is missing its work-order ID, operation number, station, supply date, or positive production quantity.

The wider canonical source contains:

- 8,286 item records; 8,273 distinct normalized item numbers.
- 2,548 sales-order headers.
- 20,392 sales-order lines; 25 have no supply date and 6 have no item.
- 9,820 production work orders, all with unique `NUMBER`; 10 have a missing/nonpositive production quantity and 2 have no supply date.
- 156,566 work-order operation rows and 156,542 distinct `NUMBER + ActionNumber` keys.
- 58,460 reusable route rows; 19 operation numbers are not valid positive integers.

## Current Meimad comparison

The current Server exposes 374 Cases, 431 Orders, 8 Case Operations, 4 Production Batches, 8 Batch Operations, and 16 Machines. Every current Case has at least one Order; only 4 Cases currently have a defined reusable route.

Of the 127 distinct items in the accessible active Kitaron projection, 100 already have an exact case-insensitive Part Number match in Meimad and 27 do not. This is strong evidence that `DetailNumber` is the appropriate fallback equivalent of the expected `ITEM_NUMBER` field. It also means the eventual sync must link existing records rather than attempting blind creation.

## Root cause of the mapping uncertainty

Kitaron and Meimad do not use the word **Order** for the same grain:

```text
Kitaron sales demand                         Meimad sales demand
TOrder + TSubOrder line          <------->   Case + Order

Kitaron production launch                    Meimad production launch
TRootCard work order             <------->   Production Batch

Kitaron work-order operation                 Meimad schedulable operation
TSubRootCard NUMBER+ActionNumber <------->   Batch Operation
```

The originally proposed key `WORKORDER_NUMBER + OPER_NUMBER` describes a work-order-specific operation. In Meimad, that is a **Batch Operation**, not a reusable **Case Operation**. Mapping it straight into Case Operations would collapse distinct production executions into a permanent route.

This is not only theoretical. Across 57,415 observed Item + Operation Number groups, 8,987 have more than one operation type, station, or description across work orders. Those differences must be reviewed rather than silently merged.

The draft therefore recommends the domain-aligned mapping:

1. Kitaron Item → Meimad Case.
2. Kitaron sales-order line → Meimad Order.
3. Kitaron production work order → Meimad Production Batch.
4. Kitaron reusable route → Meimad Case Operations.
5. Kitaron work-order operations → Meimad Batch Operations linked to the reviewed Case route.

The requested flat mapping is retained in the YAML as an unselected alternative so it can be chosen deliberately if product terminology overrides the domain model.

## Fields that should not be guessed

### Order identity

`TOrder.OrderNumber` alone is not sufficient at the target grain. There are 3,429 source Order + Item groups containing more than one line. The initial proposal uses `OrderNumber/Row`; an aggregation rule is an alternative, but must define quantity and delivery-date behavior.

### Case customer

A Meimad Case has one customer string, but 733 of 4,113 Kitaron items with work orders have production history for multiple customers. Prefer `TDetails.CustomerID` when present. Otherwise populate the Case customer only when exactly one customer is associated, or leave it for manual choice.

### Timings

Kitaron exposes several possible setup/cycle fields (`DirectionTime`, `TimeProduction`, and `...P`/default variants), but their units and precedence have not been confirmed and most route rows are empty. No seconds mapping should be enabled until Kitaron documentation or a known operation is used to validate the unit and meaning.

### Machine assignment

Kitaron stations include work centers such as 3-axis, 5-axis, inspection, engineering, and outside processing. Meimad Machines are individual physical machines. Station can help select a Meimad Machine Type, but it must not automatically assign work or reorder a Machine backlog.

### Batch allocations

Meimad requires explicit Order, stock, and scrap allocations. Kitaron exposes several quantities at sales-line and work-order grains; they are not interchangeable. Allocation mapping remains blocked until the relationship between `ProductionAmount`, the selected `TSubOrder` lines, and intentional overproduction is approved.

## Recommended next manual pass

1. Ask the Kitaron DBA to expose the intended `KitaronData2550OLAP.dbo.VProductionPlanning` metadata to the read-only login.
2. Compare its real columns with `active_projection_for_first_prototype` in the YAML and replace the fallback column names.
3. Choose the domain-aligned or flat identity model.
4. Resolve each `decision_required` item in the YAML, changing `confidence: blocked` only when validated.
5. Add a durable external-identity link table before any periodic sync. Do not rely on names or descriptions for idempotency.
6. Implement preview/diff first: new, exact match, source changed, Meimad manually changed, ambiguous, and invalid.
7. Keep synchronization one-way from Kitaron, but make all Meimad mutations Server-owned, atomic, auditable, and unable to create assignments or reorder planning.

The mapping file is intentionally editable and conservative. It contains an initial proposal, not an authorization to import.
