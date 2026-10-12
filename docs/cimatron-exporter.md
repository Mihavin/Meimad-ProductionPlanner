# Meimad Cimatron exporter

> **Status: connected to the user's NC document, no package published yet.** The exporter workflow, the package format and
> the Server import are implemented and tested with a fake Cimatron API. On the Meimad CAM PC (Cimatron 2026.0 SP3P1,
> probes of 2026-10-12) the adapter created AppAccess without a COM registration and, run from a normal window, reached
> the user's instance and NC document through `GetActiveApplication`. The seventh probe (`cimatron-com-4`) read 54
> procedures with their UCS and toolpath, all 68 named UCS with their matrices, and exported the remaining stock of the
> stock-defining procedure as a 12-triangle box. Adapter `cimatron-com-5` uses that as the initial stock (part-model
> coordinates), leaves the target-part and stock procedures out of the export, and checks the UCS matrix layout against
> `Active2ModelTranslate`. The eighth probe and the first real export (2026-10-12) showed a document with several
> setups, each with its own stock procedure and toolpath, so the export stopped at `stock_unavailable`. Adapter
> `cimatron-com-6` exports one toolpath per package (`--toolpath`), posts only that toolpath (`PostByUcsByIdVec`), takes
> the stock procedure of its setup as the initial stock, and reads Cimatron's 9-value matrix as UCS axes in rows (the
> face UCS origins of the eighth probe lie on the stock faces with their Z axis pointing outward only in that reading).
> Not yet seen: the layout check on a tilted active UCS, what `PostByUcsByIdVec` accepts as nodes, the Job Manager
> output, and a published package. The second export (2026-10-12) posted the chosen toolpath node directly and stopped
> only because the post's `Tools.html` report counted as a second NC file; the exporter now keeps it as the tool report.
> Requirement source: the Cimatron → Meimad exporter definition §1–§19 (owner, 2026-10-12).

## What it writes

`tools/Meimad.CimatronExporter` (console, .NET 10, Windows) reads the active Cimatron NC document and writes one
**simulation package** folder per Operation:

```text
<output>\cimatron-export\
  manifest.json              schemaVersion 1 (shared/Meimad.Planner.SimulationPackage/Schemas)
  package.json               {"format": "meimad-sim", "schemaVersion": 1, "packageId", "manifest"}
  nc\O1234.nc                the posted program, byte for byte as the post wrote it
  tools\tool-table.json      every T-number the NC calls (tool-table.schema.json; also a release tool table)
  tools\cutters.csv, holders.csv, materials.csv   when Cimatron provides them
  geometry\stock.stl | stock-from-op010.stl       initial or remaining stock
  geometry\fixture-001.stl …                      one file per fixture, never merged with the part
  geometry\part.stl                               the finished (target) part
  metadata\jobs.xml, coordinate-systems.json, export-log.json
  COMPLETE                   written last
```

Steps (exporter definition §11): validate the Cimatron state → write to `<output>\.staging\{guid}` → post by UCS
(`--toolpath`: `PostByUcsByIdVec` with that toolpath's node, then with its procedures when Cimatron wrote nothing,
warning `post_by_procedures`; otherwise `PostByUcsToAllNcProcedures`; fallback `ExecutePostToAllNcProcedures`, the
procedure UCS is still recorded) → tool data
→ stock (`ExportProceduresRemainingStock` on Cimatron 2026+, else `--stock-file`, else the single `STOCK*.stl` Cimatron
left beside the document or in its `NC` folder, both as a fallback with a warning) → fixtures and part (entity export, or
a Multi STL folder with `--geometry-folder`) → UCS (mode A `nc-procedure-ucs` when the geometry is in the procedure UCS,
else mode B `part-model-coordinates` from the UCS matrix; never guessed; the stock is exported in the same frame) →
manifest with SHA-256, exporter, adapter and Cimatron versions → the Server's own package validator on the staging folder
→ atomic move to `<output>\cimatron-export` (an older package there loses its `COMPLETE` and moves to
`<output>\.superseded\…`, the five newest kept) → `COMPLETE`.

**Several toolpaths.** A Cimatron NC document can hold several setups and toolpaths (Meimad's 5-axis document of
2026-10-12: three setups, each with its own stock procedure). One package is one toolpath. With exactly one toolpath it
is used; with several, `--toolpath <name>` chooses it (case-insensitive; `toolpath_not_found` otherwise), and a run
without `--toolpath` or `--procedure` stops at `toolpath_required`, listing the toolpaths with their setups. The probe
lists them too. Only the chosen toolpath's procedures are posted and checked; when Cimatron lacks `PostByUcsByIdVec`,
`--toolpath` stops at `post_selection_unavailable`.

### What the Cimatron 2026.0 API offers (adapter `cimatron-com-6`)

Read from the installed type libraries (`cimatron-api.txt`, Meimad probe 2026-10-12). The adapter looks for each
member on the NC model (`document.Model`, `INcModel`), then the document, then the application.

| Need | Cimatron 2026.0 member | Adapter |
| --- | --- | --- |
| Application | `IAppAccess.GetActiveApplication()`, `GetApplication()` (skipped while Cimatron runs: it starts a new instance) | first instance whose `GetActiveDoc` has a document |
| Document | `IApplication.GetActiveDoc`, `ICimDocument.Title`, `GetPath`; `IPdm.GetOpenDocuments` for the probe | `GetActiveNcDocument` |
| NC model | `IModelContainer.Model` | all NC members live here |
| Toolpaths | the same XML: each `ToolPath Name ID` with its `Setup Name` and the setup's stock procedure (when the setup has exactly one) | `GetToolpaths`; `--toolpath` |
| Procedures | `INcModel.GetProcessManagerAsXML(withParameters, file)`; fallback `ModelProcedures` entity list (`ICimEntity.ID`, `INCProcedure.Name`) | `ProcessManagerXml.Parse`: Cimatron 2026.0 writes `NCFullReport` → `Setup` → `SetupFolder` → `ToolPath Name` (`ToolPath_UCS`) → `ToolpathFolder` → `Procedure Name ID`, the procedure UCS in `UCS_Name Value` (else the toolpath UCS), the status in `Procedure_Status`; procedures outside a toolpath (target part, stock) are not machining and are left out of the export, a `Stock_Type` one is stock-defining; the toolpath name is the operation. Confirmed by the probe of 2026-10-12 (54 procedures) |
| UCS names | entity query on the model (`CreateFilter(cmFilterEntityType = 3)`, `IFilterType.Add(cmUcs = 9)`, `SetFilter`, `Select`; `cimatron-com-3` passed 9 to `CreateFilter`, which is `cmFilterWireBody`, and got about 100 unnamed bodies), `ActivateUcs` (the active UCS), the XML | union of the three; the fixed query returned all 68 named UCS (`IUcs` on each entity) |
| UCS matrix | `IUcs.Transformation` → `IModelTransformation.Matrix` / `Vector` | `UcsMatrix.Compose`: 16, 12, or 9 + 3 values; a 16-value matrix with the translation in the last row is transposed. Cimatron 2026.0 returns 9 + 3 values: the Vector is the UCS origin and the 9 values hold the UCS axes as rows, which `Compose` transposes into columns (deduced from the eighth probe: the face UCS such as `---90` have their origin on a stock face, and only the row reading points their Z axis out of the stock). The probe still compares the active UCS with `IModel.Active2ModelTranslate` of its origin and axes (`UcsLayoutCheck`) to confirm it on a tilted active UCS |
| Post | `PostByUcsByIdVec(post, folder, nodeIds, ucs, outputName, out result)` (selected toolpath or procedure nodes), `PostByUcsToAllNcProcedures(post, folder, startHome, ucs, out file)`, `ExecutePostToAllNcProcedures(post, folder, startHome, out file)` | the named file and every new file in the folder; `--toolpath` posts the toolpath node, then its procedure ids when no file appeared. Reports the post writes beside the NC (HTML, MHT, PDF, XML, CSV, logs, images) are never NC candidates: Cimatron's `*.Tools.html` / `*.TOOLS.mht` becomes the package's `toolReport` under `tools/`, any other report goes to `metadata/post-reports/`. Confirmed on 2026-10-12: posting the toolpath node of `TP_MODEL_0` wrote `6000.EIA` and a `Tools.html` |
| Remaining stock | `ExportProceduresRemainingStock(procId, NcUcsType, file, out created)` with `cmNcProcUcs = 1` (mode A) or `cmNcModelUcs = 3` (mode B) | the frame of the fixtures and part; earlier versions wrongly passed the UCS name here |
| Initial stock | none | `--stock-file`, else one `STOCK*.stl` in the document folder, its `NC` subfolder or the `NC` folder it sits in (`stock_ambiguous` when several); otherwise the stock procedure of the chosen toolpath's setup (or the only stock-defining procedure of the document): its remaining stock (`ExportProceduresRemainingStock`, `cmNcModelUcs`), in part-model coordinates only (mode B; mode A stops at `stock_frame_unavailable` and needs `--stock-file`). With several stock procedures and no `--toolpath`, `stock_unavailable` suggests `--toolpath`. The probe prints that STL's box |
| Cutters | `ExportCuttersAndHoldersToCSV(elements, cutters, holders, materials)` with no elements | CSV files; no tool-list API, so `tool-table.json` lists the NC's T numbers |
| Job Manager | `NcExportJobsData(outputPath, out jobs)` | given a folder: one file becomes `metadata/jobs.xml`, several stay under `metadata/jobs/` (`jobs_export_many_files`); a file path is tried when the folder stays empty |
| Part and fixture STL | none (`IGeom3DSTL` reads triangles of STL entities only) | `--geometry-folder` with Cimatron's Multi STL export; `--geometry-in-ucs` when it was written in the procedure UCS |

On any failure nothing is published: the staging folder moves to `<output>\.failed\…` with its
`metadata\export-log.json`. Every run writes `<output>\last-export-log.json` and `<output>\last-export-report.txt` (the
human-readable report, also printed). Exit codes: 0 published (maybe with warnings), 1 blocked, 2 usage error,
3 Cimatron not available.

The exporter never edits the posted G-code (exporter definition §2). The Meimad identity of the program (Case,
Operation, machine, package id) travels in the manifest; see the implementation plan for the conflict with §20.4.

## Command line

```bat
Meimad.CimatronExporter.exe --active-cimatron --output "<Case working folder>\Gcode\<Case>\<OP>" ^
  --post "HAAS_NGC_VF3" --ucs "UCS_OP010" --operation 010 --part-number "<Case>" --machine-id "VF3_01"
Meimad.CimatronExporter.exe --probe
```

`Meimad.CimatronExporter.exe` without arguments prints every option (`--stock-procedure`, `--source-operation`,
`--stock-file`, `--geometry-folder`, `--geometry-in-ucs`, `--ucs-matrix`, `--work-offset`, `--mesh-tolerance`,
`--nc-file`, `--report`, …).

Write the package below the Case Working Folder (`Gcode\<Case>\<Operation>\cimatron-export`): it is under the
network folder the Server reads, and the Server refuses packages outside its approved roots.

## Launch without manual steps

**BAT launcher** (`Launch\export-to-meimad.bat`, copied beside the exe):

```bat
export-to-meimad.bat "\\192.168.0.240\data\customers files\DPD\PN-1" PN-1 010 HAAS_NGC_VF3 UCS_OP010 VF3_01
```

**Post-processor hook (example).** If the Cimatron GPP post can run an external command after it wrote the NC, call
the launcher at the end of the post (the exact GPP syntax depends on the post and must be checked with the post
writer):

```text
; at the end of the post, after the NC file is closed
SYSTEM_CALL("\"C:\Meimad\CimatronExporter\export-to-meimad.bat\" \"" + CASE_FOLDER + "\" " + PART_NAME + " " + OP_NUMBER + " " + POST_NAME + " " + UCS_NAME + " " + MACHINE_ID)
```

The exporter posts again by UCS itself, so the hook only has to start it; the BAT orchestrates and the C# adapter reads
Cimatron (exporter definition §12 B/C).

**Folder watcher.** The Meimad Server can import complete packages by itself (`SimulationPackages:Watcher:Enabled`,
off by default; roots must be approved). It never chooses a stock or a Machine; such packages wait as `blocked` with a
report until a planner imports them from the Case.

## Importing in Meimad

Case → Operations → *Cimatron setup* → **Import Cimatron package...** (see the user help). The Server contract is in
`docs/api-contract.md` ("Cimatron simulation packages").

## Connecting to Cimatron without a COM registration

At Meimad (Cimatron 2026.0 SP3P1, probe of 2026-10-12) the API files are in
`C:\Program Files\Cimatron\Cimatron\2026.0\Program` (`CimAppAccess.dll`, `CimAppAccess.tlb`, `CimatronE.tlb`,
`CimNcAPI.tlb`, `interop.*.dll`) but `CimAppAccess.AppAccess` is not registered for COM. The exporter then makes the
AppAccess object directly from `CimAppAccess.dll` (class id from `CimAppAccess.tlb`, the DLL's class factory; nothing is
written to the registry) in the newest installed Cimatron, or the folder given with `--cimatron-folder`. The export runs
on a single-threaded apartment, as Cimatron's objects expect. A registered ProgID is still used first (`--progid` or
`MEIMAD_CIMATRON_PROGID` names another one).

With AppAccess made that way, the first call (`GetApplication`) still failed at Meimad with *Library not registered*
(`TYPE_E_LIBNOTREGISTERED`): Cimatron's objects answer IDispatch calls only when Cimatron's type libraries are
registered, and Windows needs them registered to pass calls between the exporter and the Cimatron process. Two changes
(adapter `cimatron-com-2`):

- Members are called through the vtable interfaces of Cimatron's own interop assemblies (`interop.*.dll` in the Program
  folder) when they declare them, IDispatch only as a fallback (`CimatronBinder`). Some members exist only there:
  `PostByUcsToAllNcProcedures` is on `INcModel`, not on the dispatch `_INcModel`. The adapter now goes application →
  `GetActiveDoc` → document `Model` (the NC model) and looks for each member on the model, the document and the
  application.
- `--register-cimatron` registers every type library in the Program folder: from a normal window for the current
  Windows user (`HKEY_CURRENT_USER`, no administrator rights), from an administrator window for every user of the PC
  (`HKEY_LOCAL_MACHINE`; administrator processes see only that registration). Cimatron's files are not changed;
  `--unregister-cimatron` removes the registration again. Repairing the Cimatron installation as an administrator
  registers them for every user as well.

With the type libraries registered, `GetApplication` then answered *Class not registered*: AppAccess reaches Cimatron
through Cimatron's COM classes, which are not registered on that PC either. When `GetApplication` fails, the exporter
takes the application object from the running object table instead (`CimatronRunningApplication`: by the Application
class id, else any entry with the `IApplication` interface). That table is per logon session and elevation level, so the
exporter must run from a normal PowerShell, not "Run as administrator", while Cimatron is open. If Cimatron does not put
itself there, the remaining fix is registering Cimatron's COM classes (repair the installation as an administrator).

After `--register-cimatron`, `GetApplication` connected (Cimatron 2026,0301,2064,782) but reached an instance with no
active document while the user had one open; several `CimatronE.exe` processes were running. The exporter now asks
`GetActiveApplication` first, then `GetApplication`, and takes the first instance whose `GetActiveDoc` returns a document;
the probe lists each Cimatron process's window title and start time, and the open documents of the instance it reached.

The next probe showed that `GetApplication` starts a new, empty Cimatron when it cannot reach a running one (two empty
instances, started at the probe times, beside eight user instances with NC documents), and that `GetActiveApplication`
failed with *Interface not registered* because the exporter ran as administrator, where Windows ignores per-user
registrations. The exporter therefore skips `GetApplication` while any `CimatronE.exe` runs, and its error names the
administrator cause. Run it from a normal window; with several Cimatron windows, the active one is used.

`--probe` writes everything to standard output and to `probe-result.txt` beside the exe (UTF-8, so Hebrew folder
names stay readable; no redirect is needed): the connection attempts, whether the exporter
runs as administrator, the Cimatron processes, how many of Cimatron's COM classes are registered, the whole running object
table, whether each type library is registered, how many interop interfaces were loaded, and, when connected, the
members found, the interop interfaces the application, document and NC model objects answer to, the active UCS with its
raw `Matrix`/`Vector` values and the composed 4×4 matrix, the UCS entities, the procedures read from the process-manager
XML with the stock-defining procedures, the result of exporting their remaining stock to a temporary file (triangles
and model box), the UCS layout check against `Active2ModelTranslate`, the stock STL files beside the document, the
matrices of the UCS the procedures use, and `process-manager.xml` (Cimatron's
`GetProcessManagerAsXML` with parameters, for matching the procedure and UCS accessors). It also writes
`cimatron-api.txt` beside the exe: the classes, interfaces, methods, properties and enum values of every type library in
the Program folder, read from the files (Cimatron need not run for this part). `CimatronComApi.Members` is matched to
that listing.

### Next run on the CAM PC (state of 2026-10-12, after the eighth probe and the first export)

1. Unzip the current `installer\artifacts\cimatron-exporter.zip` over `C:\cimatron-exporter\`.
2. In Cimatron, make a tilted UCS active (for example `UCS11_3`), then click the NC document's window.
3. From a normal window (Win+R, `cmd`, Enter): `C:\cimatron-exporter\Meimad.CimatronExporter.exe --probe`
   The probe lists the toolpaths with their setups and stock procedures.
4. Export one toolpath, posted relative to `MODEL` (identity matrix):
   `C:\cimatron-exporter\Meimad.CimatronExporter.exe --active-cimatron --output C:\cimatron-exporter\test-export --post Mazak_5x_Variaxis_i-500_TTCA_RTCP --ucs MODEL --toolpath TP_MODEL --operation 010`
5. Send `probe-result.txt` and `C:\cimatron-exporter\test-export\last-export-report.txt`. Put the active UCS back
   afterwards if it matters.

## Before production use

1. The process-manager XML layout and the stock-defining procedure's remaining stock are confirmed (probes of
   2026-10-12). Still to confirm: the UCS matrix layout on a tilted active UCS (the probe's layout line; the row reading
   is deduced, not yet checked), which nodes `PostByUcsByIdVec` accepts, what `NcExportJobsData` writes, that the stock box encloses the target part, and the owner's decision on procedures with
   their own UCS (implementation plan).
2. Export one real Operation in mode A (`--geometry-folder` from a Multi STL export written in the procedure UCS,
   `--geometry-in-ucs`) and in mode B (the same export in part-model coordinates); import both; check in the NC viewer
   that toolpath, stock, fixtures and part coincide (exporter definition §19 steps 1–8), on a Haas and a Mazak package.
3. Add the tested Cimatron version to `SimulationPackages:TestedCimatronVersions` on the Server.
4. Decide how the exe is installed on the CAM PCs (it is not part of the Server or client installers yet; today it is the
   zip under `installer\artifacts`).
