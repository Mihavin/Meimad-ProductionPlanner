# Legacy Excel planning import report

- **Workbook analyzed:** `data/Working plane.xlsx`
- **Observed size:** 45,497,003 bytes
- **Observed SHA-256:** `6228050C77BAEB341A6CC1EAB0B98E50EF48E371E5593D1A71B5D06D90C9FCF0`
- **Analysis boundary:** read-only multipart preview against an isolated fresh database; no commit was performed

This report records the supplied workbook analysis, its relationship to the current schema, and the conservative import boundary. The workbook name and repository-relative location are verification facts, not application defaults. The operator may select any `.xlsx` file through the wizard.

## Detected sheets

| Sheet | Rows | Columns | Detected use |
|---|---:|---:|---|
| `תכנית ייצור` | 387 | 19 | Current Machine-section planning/backlog source |
| `מס' מטוס` | 266 | 3 | Lookup/reference data; not selected for import |
| `גיליון1` | 1,263 | 43 | Open-order source |
| `קב"מ` | 174 | 13 | Reference data; not selected for import |
| `עבודות שסיימו ייצור` | 1,828 | 19 | Historical/completed work; not selected for import |

The preview selected `תכנית ייצור` as the planning sheet and `גיליון1` as the optional open-order sheet. It detected 88 bounded source-column descriptors across the workbook, 16 Machine sections, 344 planning rows, and 990 open-order rows.

## Detected columns and proposed meaning

### Planning sheet (`תכנית ייצור`)

| Column | Detected header | Preview field | Current import meaning |
|---|---|---|---|
| A | `מקט` | `customer` | Observed customer/program code; ambiguous because column B has the same header |
| B | `מקט` | `partNumber` | Case Part Number candidate |
| C | `מס'מטוס/מקט אבא` | `caseReference` | Case/customer reference candidate |
| D | `הערות` | `notes` | Source notes/context |
| F | `כמות` | `quantity` | Proposed Batch planned quantity; must be a positive whole number and be reconciled through explicit allocations |
| G | `סטאטוס חו"ג - תפ"י` | `materialStatus` | Legacy material/planning status shown as source context; not an application status mutation |
| H | `תאריך התחלה` | `startDate` | Legacy start-date context; never persisted as a calculated/planned start |
| I | `תאריך סיום` | `endDate` | Legacy finish-date context; never persisted as a calculated/planned end |
| J | `צפי אספקה -מימד` | `plannerDeliveryDate` | Planner delivery-date context |
| K | `תאריך הזמנת לקוח` | `customerDeliveryDate` | Customer delivery-date context |

Planning rows contain no reliable Operation Number, Operation Name, setup time, cycle time, QA time, or load/unload fields. The importer therefore does not invent a route. Batch creation requires an existing routed Case, snapshots that complete route, and either leaves all resulting Batch Operations in the unassigned pool or assigns one explicitly selected route Operation.

### Open-order sheet (`גיליון1`)

| Column | Detected header | Preview field | Current import meaning |
|---|---|---|---|
| A | `מספר פריט` | `partNumber` | Case Part Number candidate |
| B | `מספר הזמנה` | `orderNumber` | Order Number candidate |
| C | `מספר / שורה` | `orderLine` | Source order-line context; not a standalone domain entity |
| D | `שם לקוח` | `customer` | Case customer candidate |
| E | `תאריך / אספקה` | `deliveryDate` | Order Work Finish Date candidate after explicit operator confirmation |
| F | `REV` | `revision` | Case revision candidate |
| H | `יתרה / לאספקה` | `outstandingQuantity` | Proposed Order quantity after explicit operator confirmation |
| I | `הערות שורת הזמנת לקוח` | `notes` | Order notes candidate |
| J | `מספר שרטוט` | `drawingNumber` | Source drawing reference; preview context only in the current action set |
| K | `מספר תיק` | `caseReference` | Case/customer reference candidate |
| L | `כמות / בהזמנה` | `orderedQuantity` | Original ordered-quantity context; the operator must choose the intended production demand |
| O | `שם פריט` | `itemName` | Case name candidate |
| U | `תמונת פריט` | `picturePath` | Legacy external picture-path context; not copied or opened by the import service |

Column mappings are proposals. The wizard keeps each target field stable and lets the operator choose a different source column before validation and review.

## Sample value categories

Representative planning values included:

- Part Numbers such as `30P410132102-001` and `4341-1066-001`;
- quantities such as `23` and `10`;
- Case references such as `3013` and `MABAT`;
- material states and free-text notes in Hebrew;
- dates supplied as Excel serials or cached formula results and normalized in preview to ISO `yyyy-MM-dd`.

Representative open-order values included:

- Order Numbers such as `E000387229` and `KPO234215`;
- line identifiers such as `282` and `1`;
- positive outstanding and ordered quantities;
- revisions such as `NEW` and `A`;
- Case references, item names, drawing references, and legacy UNC picture paths.

Quantities are parsed only as positive whole numbers. Dates are accepted from the Excel 1900 date system or exact ISO `yyyy-MM-dd`. The Server does not evaluate formulas or follow external workbook relationships; it uses only a formula cell's cached value and retains its provenance.

## Machine sections

The planning sheet exposed these 16 source sections:

1. `מכונה 1 - 3 צירים`
2. `מכונה 2 - 4 צירים`
3. `מכונה 3 - 4 צירים`
4. `מכונה 4 - 3 צירים -MAXIMART`
5. `MAZAK 5 - צירים5`
6. `MAZAK 6 - 5 צירים`
7. `DOOSAN 7 - 5צירים`
8. `NEW מכונה8 - 3 צירים`
9. `מכונה 10 HAAS - צירים3`
10. `HAAS חדשה 14 -5צירים`
11. `HAAS חדשה 15 -5צירים`
12. `OKUMA 9 חריטה`
13. `DOOSAN חריטה 11`
14. `חריטה 12 חדש -HAAS`
15. `חריטה 13 חדש -CHEVALIER`
16. `מכונה קונבנציאונלית`

The isolated preview database intentionally contained no matching production Machines, so all 16 sections required explicit mapping. A Machine is never created from a label. A clear candidate still requires an operator click; an unknown Machine may be handled by skipping the assignment or creating the Batch in the unassigned pool.

## Unclear fields and validation findings

The supplied workbook produced 66 structured issues: 5 blocking issues and 61 warnings.

| Count | Severity | Code | Meaning/action |
|---:|---|---|---|
| 30 | Warning | `source_cell_error` | Excel error cells are retained with provenance; review or remap before using the affected value |
| 16 | Warning | `machine_mapping_required` | Every source Machine section needs an explicit registered-Machine choice in the isolated database |
| 14 | Warning | `invalid_date` | Source value was neither a valid Excel serial nor ISO date |
| 4 | Blocking | `quantity_required` | A Part Number was present without quantity; map a valid quantity or Skip the row |
| 1 | Blocking | `invalid_quantity` | A mapped source quantity was not a positive whole number; correct/remap it or Skip the row |
| 1 | Warning | `duplicate_source_row` | A planning row duplicated the source fingerprint and needs an explicit decision |

Additional interpretation points requiring operator confirmation are:

- columns A and B on the planning sheet share the header `מקט`, while their observed values have different meanings;
- `מס'מטוס/מקט אבא`, planner delivery, customer delivery, ordered quantity, and outstanding quantity are source concepts, not permission to overwrite an existing Case or Order;
- the workbook's start/end dates are historical planning context, not authoritative actual times or persisted new planned dates;
- Operation identity and route timing are absent, so a source row cannot safely create a new route;
- cached external-formula values may be stale, and a missing/error cache is never recalculated by the Server.

Of the 344 surfaced planning rows, 340 contained both a Part Number and quantity. The four remaining Part-only rows stayed visible with blocking issues; none was silently discarded.

## Current database entity mapping

| Application concept | SQLite table | Import behavior |
|---|---|---|
| Case | `cases` | Match by normalized Part Number; create only from a complete explicit action |
| Order | `orders` | Create under an explicit Case with Order Number, positive quantity, and Work Finish Date |
| Production Batch | `production_batches` | Create only for an existing routed Case and an explicit Batch Number/allocation plan |
| Batch Allocation | `batch_allocations` | Create explicit Order, stock, and/or scrap-allowance rows whose total equals Batch quantity |
| Case Operation | `case_operations` | Read as the authoritative existing route; never invented or updated from this workbook |
| Batch Operation | `batch_operations` | Snapshot the selected Case's complete existing route when a Batch is created |
| Machine | `machines` | Candidate lookup only; never auto-created |
| Machine Assignment | `machine_assignments` | Create only for an explicitly selected unassigned Operation and registered active Machine; append after existing backlog rows |
| Machine Type | `machine_types` | Read for compatibility evidence; never created or changed by import |
| Working Calendar | `working_calendars` | Existing Machine relationship remains unchanged; not imported from the workbook |
| Import receipt | `legacy_working_plan_imports` | Store one durable committed-workbook/idempotency receipt in schema v25 |

Existing records are shown as candidates/matches. The current importer has no silent update action: it does not overwrite existing quantities, dates, notes, routes, setup/cycle times, statuses, actual times, pause history, planning modes, Machine choices, or backlog positions.

## Staging and commit architecture

Preview staging is transient, bounded Server memory, not a production-table write:

```text
selected .xlsx stream
→ bounded OpenXML parse
→ in-memory staged workbook/candidate snapshot
→ structured validation and preview
→ explicit operator mapping and row decisions
→ Edit-Mode-gated atomic commit
→ canonical entities plus durable idempotency receipt
```

There are no `ImportSession`, `ImportStagingRow`, or `ImportMapping` SQLite tables. Instead, the Server retains at most four parsed previews for a configurable lifetime (120 minutes by default). The token expires on timeout/eviction/restart before a first commit. Schema v25 persists only `legacy_working_plan_imports` fields for the successful receipt: workbook hash, approved-request hash, response JSON, committing client/user, and commit time. It stores neither workbook bytes nor source rows.

Commit rebuilds the approved preview from the staged source, revalidates all explicit decisions and current domain facts, then writes canonical records and the receipt in one immediate SQLite transaction. Any invalid selected row rolls back the complete commit. Exact replay returns the receipt without duplicate creation; a different approval for an already committed workbook is rejected.

## UI and service boundary

The Setup page uses one five-step wizard:

1. Preview workbook.
2. Choose Orders, unassigned Pool Batches, and/or Machine assignments.
3. Confirm/correct columns and Machine mappings.
4. Resolve individual rows and optionally apply reviewable safe patterns.
5. Review every decision and commit.

The Windows client uploads the operator-selected stream and renders Server results. Parsing, validation, candidate matching, idempotency, domain rules, and the atomic commit remain Server-owned. Commit is unavailable until Review, all selected rows are resolved, and the client holds Edit Mode.

## Manual verification before production import

1. Back up the current Server database and verify the backup.
2. Restore/rehearse against an isolated database copy.
3. Preview the operator-selected workbook and verify the five sheet names/dimensions.
4. Review every proposed column and all 16 Machine-section mappings.
5. Resolve or Skip all 344 planning rows and all selected open-order rows; inspect every warning.
6. Confirm that every Batch action uses the intended existing Case route, allocation quantities, and unique Batch Number.
7. Commit only in the rehearsal database, then reconcile Case/Order/Batch/Operation counts, unassigned pool contents, and every affected Machine backlog.
8. Preview/replay the same approved workbook and verify no duplicate records or backlog changes.
9. Obtain operator sign-off before repeating the approved workflow against the backed-up production database.

## Known limitations

- Only `.xlsx` is supported; formulas and external links are not evaluated.
- Preview staging is memory-only until successful commit and must be recreated after expiry, eviction, or restart.
- The importer creates no raw Excel-defined Operation route because this workbook has no trustworthy route/timing fields.
- Existing domain records are matched but not silently updated; conflicting differences require a separate supported edit workflow.
- Machine mapping and cross-type override remain explicit human decisions.
- The real-workbook smoke proves read compatibility only. Production commit, visual Windows acceptance, and operator reconciliation remain manual and intentionally incomplete.
