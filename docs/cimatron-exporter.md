# Meimad Cimatron exporter

> **Status: UNVERIFIED against Cimatron.** The exporter workflow, the package format and the Server import are
> implemented and tested with a fake Cimatron API. The Cimatron connection (`CimatronComApi`) has never run on a PC
> with Cimatron: the method names of the exporter definition (§3) are used as given, and the procedure, UCS, tool and
> geometry accessors are best guesses. Run `--probe` on a Cimatron PC first and adjust
> `CimatronComApi.Members` before any production use. Requirement source: the Cimatron → Meimad exporter definition
> §1–§19 (owner, 2026-10-12).

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
(`PostByUcsToAllNcProcedures`; fallback `ExecutePostToAllNcProcedures`, the procedure UCS is still recorded) → tool data
→ stock (`ExportProceduresRemainingStock` on Cimatron 2026+, else the initial stock, else `--stock-file` as a fallback
with a warning) → fixtures and part (entity export, or a Multi STL folder with `--geometry-folder`) → UCS (mode A
`nc-procedure-ucs` when the geometry is in the procedure UCS, else mode B `part-model-coordinates` from the UCS matrix;
never guessed) → manifest with SHA-256, exporter, adapter and Cimatron versions → the Server's own package validator on
the staging folder → atomic move to `<output>\cimatron-export` (an older package there loses its `COMPLETE` and moves to
`<output>\.superseded\…`, the five newest kept) → `COMPLETE`.

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
- `--register-cimatron` registers every type library in the Program folder for the current Windows user
  (`HKEY_CURRENT_USER`, no administrator rights; Cimatron's files are not changed); `--unregister-cimatron` removes them
  again. Repairing the Cimatron installation as an administrator registers them for every user instead.

With the type libraries registered, `GetApplication` then answered *Class not registered*: AppAccess reaches Cimatron
through Cimatron's COM classes, which are not registered on that PC either. When `GetApplication` fails, the exporter
takes the application object from the running object table instead (`CimatronRunningApplication`: by the Application
class id, else any entry with the `IApplication` interface). That table is per logon session and elevation level, so the
exporter must run from a normal PowerShell, not "Run as administrator", while Cimatron is open. If Cimatron does not put
itself there, the remaining fix is registering Cimatron's COM classes (repair the installation as an administrator).

`--probe` writes everything to standard output (redirect it to a file): the connection attempts, whether the exporter
runs as administrator, the Cimatron processes, how many of Cimatron's COM classes are registered, the whole running object
table, whether each type library is registered, how many interop interfaces were loaded, and, when connected, the
members found and `process-manager.xml` (Cimatron's `GetProcessManagerAsXML`, for matching the procedure and UCS
accessors). It also writes `cimatron-api.txt` beside the exe: the classes, interfaces, methods, properties and enum values
of `CimAppAccess.tlb`, `CimatronE.tlb`, `CimNcAPI.tlb`, `CimBaseAPI.tlb`, `CimServicesAPI.tlb` and `CimMdlrAPI.tlb`, read
from the files (Cimatron need not run for this part). `CimatronComApi.Members` is matched to that listing.

## Before production use

1. Run `--probe` with an NC document open; record which members exist on the application and the document.
2. Adjust `CimatronComApi.Members` and the accessors (procedures, UCS matrix, tools, geometry export) to the installed
   Cimatron version; bump `CimatronComApi.Version`.
3. Export one real Operation in mode A and in mode B; import it; check in the NC viewer that toolpath, stock, fixtures and
   part coincide (exporter definition §19 steps 1–8), on a Haas and a Mazak package.
4. Add the tested Cimatron version to `SimulationPackages:TestedCimatronVersions` on the Server.
5. Decide how the exe is installed on the CAM PCs (it is not part of the Server or client installers yet).
