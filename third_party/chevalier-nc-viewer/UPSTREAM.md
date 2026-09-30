# Chevalier NC viewer (vendored)

These files are an **unmodified** copy of part of the ChevalierGcode project
(`C:\VisualCodeWork\ChevalierGcode\fanuc-toolpath-preview`, "CNC Toolpath Preview",
Meimad Metals, internal).

| | |
|---|---|
| Upstream version | 0.17.0 (`package.json`) |
| Upstream commit | `9e2645cddac22ae9538970c83809300e3e30faa3` (2026-09-23, "Home offsets") |
| CodeMirror | 5.65.21 (MIT, `vendor/codemirror/LICENSE`) |
| three.js | 0.185.1 (MIT, `vendor/three/LICENSE`) |

Do not edit these files. One local exception: `machines/haas-umc-500.json` carries the factory's
measured MRZP (owner change 2026-09-30, `verified: true`); a sync must keep those values or the
upstream placeholder returns. Meimad-specific behavior lives outside this folder:

- `shared/Meimad.Planner.NcEngine/Scripts/bootstrap.js` — CommonJS loader and the
  `node:path` (win32), read-only `node:fs` and `Buffer` shims the engine needs in V8.
- `shared/Meimad.Planner.NcEngine/Scripts/meimad-nc-engine.js` — the adapter the .NET host
  calls (analysis, preview parse, tool table, NC-dialect interpreter choice).
- `shared/Meimad.Planner.NcEngine/Scripts/meimad-dialects.js` — translations for controls the
  interpreters do not read natively (Okuma OSP-P200L lathe, Haas lathe one-block cycles, Mazak
  Variaxis A tilt as B), with a line map back to the original program.
- `shared/Meimad.Planner.NcEngine/Scripts/meimad-macro.js` — the custom macro executor for lathe
  programs (the vendored lathe interpreter reads only simple assignments and IF..GOTO): local and
  common variables, system variables, WHILE/DO/END, IF..GOTO/THEN, GOTO, G65/G66/G66.1/G67,
  M98/M97/M99 with calls from the program folder or the machine's program memory folder, and
  FANUC custom macro calls by G/M/T codes. It hands the interpreter a flat program with a line map,
  the program stops (M00/M01/#3006) and the DPRNT output. For mills, `meimad-nc-engine.js` wraps
  three methods of the vendored mill interpreter's prototype at run time (never the file) to
  report program stops, the called program of every segment and the custom macro calls.
- `shared/Meimad.Planner.NcEngine/Definitions/` — Meimad machine and control definitions
  (Mazak Variaxis i-500, Okuma Genos L200E-M, Haas ST-25Y, Haas VF-3SS, generic FANUC 0i-MC
  3-axis and 4-axis A mills) in the vendored JSON schema; loaded through `loadRegistry` next
  to the vendored `machines/` and `controls/`.
- `client-windows/Meimad.Planner.Client.Windows/NcViewer/` — the Meimad viewer page
  (`meimad-viewer.html`), the WebView2 bridge that replaces the Electron preload, and the
  Meimad additions (read-only release mode, Apply Meimad Planner Format, Use for G-code release,
  program-memory folder rows for lathes).

## What is included

| Folder | Used by | Content |
|---|---|---|
| `src/` | Server and client (`nc-engine\`) | parser (FANUC lathe), haas-mill (Haas NGC / FANUC 30i mill), macro, machines, program-model, subprograms, tool-table, machine-parameters, stroke-font |
| `media/` | engine: `kinematics.js`, `model-transport.js`; client page: all | viewer page scripts and 3D viewer |
| `desktop/` | client page | `renderer.js`, `desktop.css`, `fanuc-mode.js` (CodeMirror mode) |
| `machines/`, `controls/`, `CNC-PARA.TXT` | engine | machine and control definitions, FANUC parameter export |
| `vendor/codemirror`, `vendor/three` | client page | published as `NcViewer\node_modules\...` (the upstream relative layout) |

Not included: the Electron main process and preload, the VS Code extension, AI tool
recognition and Codex (`src/ai-*.js`, `reference-workspace.js`), the STEP viewer and
polyarc envelope (`step-*.js`, `polyarc-overlay.js`), tests, release binaries.

## Known upstream limitations handled by the adapter

- The FANUC lathe interpreter does not implement `G04`: `G4 X1.5` is drawn and timed as a feed
  move to X1.5. The Meimad adapter turns dwell-only lathe blocks into comments before
  interpretation (line numbers unchanged) and adds their time from the program text. Remove
  that workaround when upstream implements lathe dwell.
- The lathe interpreter does not resolve `M98`/`G65`/`M97` and executes only simple macro
  statements; `meimad-macro.js` executes the program first (with the upstream
  `createSubprogramResolver` search order for called programs).
- The lathe interpreter reads FANUC two-block cycles only; the mill tilt solver moves B/C only.
  `meimad-dialects.js` rewrites Haas one-block cycles, Okuma OSP syntax and the Mazak A tilt.
  Remove a translation when upstream reads that control natively.
- The settings dialog lists program-memory folders for mills only; `meimad-viewer.js` adds the
  same rows for lathes.

## Updating

Run `scripts/sync-chevalier-nc-viewer.ps1 -Source <path to fanuc-toolpath-preview>`, update
the version/commit above and `NcEngineInfo.UpstreamVersion` in
`shared/Meimad.Planner.NcEngine/NcEngineModels.cs`, then run the Server and client tests. A new
upstream version changes the NC analysis parser version, so the Server re-analyzes every
release once in the background (`NcAnalysisBackfillService`) and the new estimates replace the
old ones in the Planning Board and Timeline.
