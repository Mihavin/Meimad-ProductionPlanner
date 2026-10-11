# PRD — Machine Designer and minimal VMID-like machine definition

**Status:** Proposed product requirements; implementation NOT yet started by this PR.  
**Date:** 2026-10-11  
**Repository:** Mihavin/Meimad-ProductionPlanner  
**Product area:** Windows Production Planner + embedded Meimad NCViewer / NC Engine  
**Audience:** Codex implementation agent and reviewer

## 1. Outcome

Implement a dedicated graphical **Machine Designer** in Meimad Production Planner, responsible for creating and editing the complete machine-simulation definition for each physical Machine. Treat a machine as a hierarchy of actual moving and static components, comparable to the useful structural part of a SolidCAM VMID, **not** a clone of SolidCAM's complete VMID or its postprocessing system.

The machine has one canonical, versioned server-held definition, including kinematics, appearance, geometry asset references, motion limits and timing data, tool magazine capacity and tool-change duration. Meimad NCViewer consumes this definition to display a realistic moving machine. No settings should have two independently editable authoritative copies.

**User success:** A planner can create/edit a machine, import separate 3D components, assemble them at all-axes-zero, define travel and rotational axes, jog them, save the model, and open an NC program with both the correct moving geometry and the correct physical axis readout.

## 2. Confirmed current code (inspect before editing)

- Physical Machines and editable legacy parameters live in the Server model and database: server/Meimad.Planner.Server/Domain/Machines/Machine.cs, Persistence/SqliteMachineRepository.cs. Existing fields include usableToolPositions, rapidRateMillimetersPerMinute, toolChangeTimeSeconds, machineTimeFactor and ncViewerMachine.
- Windows Setup UI currently edits those parameters: client-windows/Meimad.Planner.Client.Windows/Presentation/SetupViewModel.cs and Views/SetupView.xaml, around the Machine form (the exact line numbers may move).
- Installed NC Engine machine definitions are JSON files loaded via shared/Meimad.Planner.NcEngine/NcEngineMachineCatalog.cs. Example: shared/Meimad.Planner.NcEngine/Definitions/machines/mazak-variaxis-i-500.json. These currently contain axes, kinematics.nodes, toolChanger, primitive geometry and mostly null external model references; some coordinates are explicitly unverified placeholders.
- The embedded viewer is a WebView2 integration using client-windows/Meimad.Planner.Client.Windows/NcViewer/meimad-simulation.js and meimad-viewer3d.js; it wraps a vendored viewer. meimad-simulation.js calls CncKinematics.buildChain on machineDefinition. Existing NC interpretation must be reused, not replaced.
- The embedded viewer explicitly does NOT include the standalone viewer's STEP functionality: see docs/architecture.md. Implement real geometry loading through a deliberate new asset path.
- The Mazak VARIAXIS i-500 built-in definition currently maps its physical A tilt to interpreter B. Do not accidentally show the internal B name as the physical machine-axis label when A is what the actual machine uses.
- Respect root AGENTS.md: only Server writes SQLite; optimistic concurrency; Windows editing permissions; don't modify vendored third_party; update affected docs/contracts/tests.

These are integration points, not claims that Machine Designer or real machine-mesh loading already exist.

## 3. Approved scope and modeling decisions

### 3.1 Machine zero/reference pose — mandatory

- A Machine definition is authored with **every physical axis exactly 0**. Component geometry and axis location/direction are entered in that common reference pose.
- All minimum and maximum travel values are absolute axis readings relative to that reference zero, in mm for linear axes and degrees for rotary axes. Allow e.g. X: -500..0 or A: -120..+30. Zero must be within the allowed interval unless the machine definition explicitly declares and justifies an unusual axis configuration; default validation requires zero to be included.
- Use one explicit right-handed machine coordinate system. Geometry imports must be aligned into the zero-pose assembly; preserve a per-mesh alignment transform if original CAD coordinates differ.
- When all axis values are zero, no hidden translation/rotation may be introduced just to make the simulated home look nicer. The Machine Designer's "Zero all axes" button returns to the defined assembly.

### 3.2 Kinematic joints — mandatory

- **Linear axis:** axis name, **direction vector only**, signed min/max and speed. Do NOT ask for a point on a linear axis.
- **Rotary axis:** axis name, **one point lying on its rotation line AND a direction vector**, signed min/max or continuous/wrap policy, and angular speed. Do NOT ask for an additional pivot/rotation-center field.
- Normalize direction vectors after validation (finite, nonzero). The vector represents positive physical motion; rotary positive angles follow a documented right-hand convention. If a controller needs remapping, adapt its axis readings at the NC integration boundary, not by creating two machine geometry definitions.
- Parent/child order must be explicit and acyclic. Axis point/direction and component's zero-pose transform belong to the parent node's coordinate system as assembled at machine zero. This rule is essential for nested A/C trunnions.
- Kinematics: linear transform = translation along normalized direction by q; rotary transform = translate(point) * rotate(direction, q) * translate(-point); compose ancestors in tree order, with q=0 giving the exact authored reference pose.
- Intersections between rotation lines, a displayed MRZP/rotary center or a tool center may be **derived** from the zero-pose geometry when meaningful; they are NOT additional required authoring inputs. If axes are skew/non-intersecting, do not pretend they have a unique common intersection.

### 3.3 Machine hierarchy, mounts and meshes — mandatory

- Tree of static and moving components (e.g. Base -> X carriage -> Y ram -> Z head -> spindle; Base -> A trunnion -> C platter), with stable IDs, parent, label, optional joint and one or more geometry assets.
- Allow an explicit tool/gauge-line mount frame on a component and an explicit workpiece/fixture mount frame on a component. Mounts follow their node through the whole chain. These are poses/attachment frames, not merely boolean labels.
- Each mesh: relative asset reference, alignment transform (as needed), display name, color override, visible, opacity 0..1, optional wireframe, collisionParticipation. **Visibility/opacity MUST NOT implicitly disable collision participation.**
- Support at least GLB as the preferred runtime format; STL import is helpful if already feasible. Source STEP is not assumed renderable in the embedded NCViewer. STEP-to-mesh conversion is an explicit import/conversion workflow, or a later enhancement, never silently represented as working.
- Do not embed large geometry blobs into database JSON. Store assets in a Server-managed Machine asset area with bounded upload size, validated extension/MIME, content hashes and **portable relative paths**, never arbitrary client absolute paths. The viewer obtains only the assets of the selected authorized machine through the existing controlled host/Server bridge.
- On missing/bad assets: visibly report which mesh failed; show available geometry/primitive fallback; never crash or silently pretend the definition is fully loaded.

### 3.4 Motion, tool magazine and timing — mandatory

- Per-axis min/max, linear rapid (mm/min) or rotary rapid (deg/min), and optional max programmed feed. Physical axes' current values are displayed with axis name and the correct units.
- Tool changer data: magazine nominal capacity, **usable positions** (effective operational limit), tool-change duration seconds; optional tool-change safe position if simulation of that motion is supported. Validate 0 < usable <= nominal when both are known.
- Preserve the existing physical Machine's operational defaults: rapidRateMillimetersPerMinute, toolChangeTimeSeconds, machineTimeFactor and usableToolPositions. Expose these via the *same new Machine Designer* (e.g. Timing & Magazine tab), and do not keep other independently editable forms for the same values.
- Preserve ToolCapacityEvaluator/production readiness checks and NcCycleTimeEstimator behavior across migration. Do not silently change historical cycle estimates from the scalar rapid-rate model: only switch estimation algorithms as a separate, tested decision.
- The NC Engine may retain built-in controller/interpretation constants and safe fallbacks, but user-editable values for an actual Machine must have exactly one effective authoritative source.

### 3.5 Accurate machine-axis readout — mandatory

- Show the **physical Machine axis coordinates**, not inferred drawing positions of meshes or solely programmed WCS coordinates.
- Respect reference-zero offsets, positive directions, machine absolute vs work coordinates, the G54/G55/... work offset, G53 and G28/G30 returns, and 5-axis axis naming/adapters. The work offset does NOT redefine machine-axis zero.
- If absolute axis positions cannot be determined from available NC input (e.g. unknown work offset, unknown G54.2/TCP calibration), **mark the readout as unresolved/estimated and explain why**. Never display fabricated "exact machine coordinates".
- Playback must drive the same axis state used to animate all geometry; slider/jog preview and NC playback use one shared kinematic evaluator.
- For the Mazak Variaxis physical A axis translated internally to B, show **A** on the physical display while preserving whatever interpreter mapping is required behind the scenes.
- Axis limits must be checked against computed physical axis states with explicit messages; distinguish "not verified because reference offsets are unknown" from "within limits".

### 3.6 Collisions — scoped honestly

- The machine definition must store per-mesh collisionParticipation and (optionally) grouping/ignored-pair settings.
- Required in first release: the metadata is editable and round-trips correctly; axis travel-limit validation is active.
- A reliable general mesh-vs-mesh/holder/fixture collision solver is a separate implementation milestone unless an existing engine can demonstrably perform it. If not implemented, say "Machine collisions not checked" in the viewer, not "Collision-free".

## 4. Canonical data / ownership

Keep the existing physical Machine object/identity and planning links. Introduce one server-authoritative, schema-versioned **MachineSimulationDefinition** attached to MachineID. It may be initialized from an installed model/template but represents the actual physical machine after editing. The format need not be XML: the requested capability is the *VMID-like machine structure*. JSON is a reasonable internal canonical representation because the existing engine already speaks JSON. XML import/export may be added later, but must not create a second authoritative editable model.

Suggested fields:

- identity: schemaVersion, machineId, name, units=mm, angles=deg, handedness, referencePose=allAxesZero, definitionVersion.
- nodes: stable nodeId, parentId, label, zeroPoseTransform, optional joint with physical axisName, type, direction, **point for rotary only**, limits/speeds/wrap.
- geometryAssets manifest and per-node geometry instances with source path, material/display options and collision flag.
- mounts: toolMount and workpieceMount with nodeId and local frame.
- magazine: nominalCapacity, usablePositions, toolChangeSeconds, optional safe position.
- timing: legacy scalar rapidRateMillimetersPerMinute, machineTimeFactor and any distinct per-axis speed data.
- calibration/provenance: verified/unverified status for zero, rotary lines and mounts; human notes, who/when last changed. Do not turn unverified placeholders into exact values.

A short illustrative fragment (proposed, NOT current schema):

~~~json
{
  "schemaVersion": 1,
  "machineId": "existing-planner-machine-id",
  "units": "mm",
  "angleUnits": "deg",
  "referencePose": "all-axes-zero",
  "nodes": [
    {"id": "base", "parent": null, "label": "Base", "models": [{"asset": "geometry/base.glb", "visible": true, "opacity": 1.0, "color": "#808080", "collisionParticipation": true}]},
    {"id": "z-head", "parent": "base", "label": "Head", "joint": {"axis": "Z", "type": "linear", "direction": [0,0,1], "min": -600, "max": 0, "rapidRate": 30000}, "models": [{"asset": "geometry/head.glb", "visible": true, "opacity": 1.0, "collisionParticipation": true}]},
    {"id": "spindle", "parent": "z-head", "label": "Spindle"},
    {"id": "a-trunnion", "parent": "base", "label": "A trunnion", "joint": {"axis": "A", "type": "rotary", "point": [0,0,0], "direction": [1,0,0], "min": -120, "max": 30, "rapidRate": 18000}},
    {"id": "c-platter", "parent": "a-trunnion", "label": "C platter", "joint": {"axis": "C", "type": "rotary", "point": [0,0,0], "direction": [0,0,1], "continuous": true, "rapidRate": 18000}, "models": [{"asset": "geometry/platter.glb", "visible": true, "opacity": 0.8, "collisionParticipation": true}]}
  ],
  "mounts": {
    "tool": {"node": "spindle", "position": [0,0,0], "rotationDeg": [0,0,0]},
    "workpiece": {"node": "c-platter", "position": [0,0,0], "rotationDeg": [0,0,0]}
  },
  "magazine": {"nominalCapacity": 60, "usablePositions": 60, "toolChangeSeconds": 5.0},
  "timing": {"rapidRateMillimetersPerMinute": 30000, "machineTimeFactor": 1.0}
}
~~~

All lengths in the example are illustrative, **not calibrated values of a real Mazak, Haas or any other specific machine**. The full required contract will also define zero-pose node transforms, asset identifiers/checksums, validation rules and axis physical-name mapping.

No rotary-point field for the linear Z axis. No separately editable MRZP or pivot. The C axis is defined in its parent A trunnion's zero-pose frame.

## 5. Windows Machine Designer UI

Implement a separate Machine Designer window/page, launched from the existing Machine list in **Production Planner**. NCViewer is a read-only consumer of the saved definition, not the place to author it.

Recommended layout:
1. **Left — hierarchy:** Base, axis components, fixed components, spindle, table; add, delete, duplicate, reparent with validation; distinguish linear/rotary/static/mount nodes.
2. **Center — interactive 3D assembly:** orbit/pan/zoom, select/highlight, axes/gizmos, show axis line and zero-pose mesh transforms, jog individual physical axes, "Zero all axes", limits, visual error markers.
3. **Right — property inspector:** selected node's parent, joint type/name, linear direction or rotary point+direction, min/max/rapid, mount frames, geometry file, color, opacity, visibility, collision participation.
4. **Machine tabs:** General (existing Machine identity), Kinematics, Geometry, Timing & Tool Magazine, Validation. Existing calendar, permissions and planning-specific settings can remain in their current domain UI; only duplicate *machine-definition* fields move.
5. **Actions:** New from template / blank, Import meshes, Validate, Save draft, Publish for simulation, Cancel. Unauthorized user = view-only. Version conflicts explain stale server state; never overwrite edits silently.
6. Show calibrated/unverified markers for actual axes/reference points. Provide per-axis readout in mm or deg while jogging. All axis values start at zero.

Use the Windows UI patterns already present in Setup; do not embed a new unrelated web app unless that is demonstrably a simpler supported design.

## 6. Persistence/API and NCViewer adapter

- Server is the sole SQLite/file authority. Add a migration with a machine-definition table/JSON and a secure asset store plus explicit API endpoints to GET, PUT (optimistic expectedVersion), validate, upload/read assets, and optionally export/import a portable package. Restrict write operations to existing Machine setup permissions.
- Existing Machine list/API and NC analysis must keep working for all old machine rows and built-in definitions without any manual migration step.
- During migration/backfill, carry over **all** existing usable tool positions, legacy rapid time, tool-change time, time factor, and ncViewerMachine selection. Preserve nullable unknowns and auto-detect instead of making up physical values. Keep old columns as read-only projections/compatibility until every caller is migrated; have ONE write path to the new canonical definition and do not allow split-brain values.
- Adapter compiles/translates the saved definition into the shapes used by existing CncKinematics.buildChain and NC Engine machine registry. Prefer adapting at the Meimad wrapper/registry layer, leaving third_party/chevalier-nc-viewer untouched.
- Resolve the actual Machine's definition by stable MachineID when a viewer window opens and when Server NC analysis runs; continue to support built-in catalog fallback and existing NC dialect selection. The Server and client must resolve the same version/revision of a machine definition for a given simulation, or explicitly report when versions differ.
- Rendering asset transport must conform to the isolated WebView2 origin/host boundary; do not give arbitrary browser JavaScript unrestricted disk or network reads.
- Keep simulation timeline, stock removal and toolpath behavior intact. Move only machine structure/rendering/state integration in this feature; do not rewrite the NC interpreter or postprocessor.

## 7. Implementation plan for Codex (work in vertical slices)

**Slice A — Contract / migration / tests**
- Inspect AGENTS.md, docs/functional-spec.md, docs/architecture.md, docs/data-model.md, docs/api-contract.md.
- Add a versioned machine-definition schema, validation, server persistence, API contract and tests; backfill legacy Machine values without changing planning calculations.
- Add reference-frame math and serializable hierarchy; prove zero-pose, signed limits, linear and rotary joint math with unit tests.

**Slice B — Machine Designer basics**
- New dedicated Windows Machine Designer opened from the existing Machine record.
- Tree editor, joint property editor, zero-pose behavior, jog/reset, save/validation and concurrency handling.
- Move magazine/timing authoring into this UI, remove duplicate editable controls from original Setup Machine form (read-only summary/link acceptable). Keep planning outputs unchanged.

**Slice C — Geometry loading / rendering**
- Server asset manifest/transfer, GLB loader in the embedded viewer, per-component attachment, color/opacity/visible/collisionParticipation.
- Controlled import/align to zero pose; errors and missing-asset fallback; test model-independent animation.
- Add at least one realistic sample 3-axis model and one 5-axis table-table model, with geometry used only if legally and technically available. Otherwise use nonmisleading synthetic labeled test meshes.

**Slice D — NC playback / axis readout**
- Wire the actual saved physical Machine to the existing NC Engine/wrapper.
- Apply its physical axis state to kinematic tree and display real-axis X/Y/Z/A/B/C values at each playback step; handle the physical Mazak A vs interpreter B mapping.
- Verify G53, WCS and TCP examples; report unknown calibration/offset instead of exact-looking coordinates.
- Add axis travel-limit checks and explicit collision-not-checked label until actual collision verification is implemented.

**Slice E — cleanup/docs/regression**
- Remove/disable competing authoring points for the relocated machine parameters while preserving existing external API compatibility.
- Update docs/functional-spec.md, docs/data-model.md, docs/api-contract.md, docs/architecture.md, docs/user-help.md, docs/implementation-plan.md and relevant tests.
- Run full relevant tests and build. Report what works and what remains unimplemented or unverified. Avoid unrelated features.

## 8. Acceptance criteria (testable)

- [ ] New physical Machine can be given a saved, versioned hierarchy in the Windows Machine Designer and reopened without loss.
- [ ] Linear axis editor requires direction, signed limits and speeds; does **not** demand or save rotary point.
- [ ] Rotary axis editor requires point+direction, signed angular limits or continuous policy; does **not** demand a separate center/pivot.
- [ ] Zeroing all joints yields precisely the authored zero-pose assembled components; X=-100 and A=+30 yield mathematically expected transforms; nested A then C composes in the correct order.
- [ ] Axis jog respects min/max and direction; machine physical coordinate readouts equal the actual axis values, with proper mm/deg units.
- [ ] NC playback and geometry use the same physical axis state. Unknown machine coordinates are labeled unresolved rather than presented as real values; the physical Mazak A is not mislabeled B because of engine translation.
- [ ] GLB meshes survive save/reopen, move with their owning nodes, and retain color, visibility, opacity and collisionParticipation independently. Bad/missing assets show an error.
- [ ] Tool and workpiece mount frames are supported and follow correct branches during 5-axis motion.
- [ ] Only Machine Designer can edit machine timing and magazine values. Existing machines retain identical operative limits and timing estimates after migration, absent an intentional user edit.
- [ ] Legacy NC Engine machine definitions and existing preview work for machines with no new geometry, without breaking releases/readiness/planning.
- [ ] Validation rejects duplicate axes/node IDs, cyclic or missing parents, non-finite numbers, zero direction vector, limits that violate the zero-reference rule, dangerous asset paths and bad capacities.
- [ ] Server permission and optimistic concurrency are enforced; no client SQLite access and no third_party source edits.
- [ ] UI does not report "no collision" unless a real collision engine executed successfully; axis-limit violations do report.
- [ ] All new/changed contracts, migrations, UI logic and kinematics have tests; docs reflect implemented behavior rather than presenting the PRD as implementation.

## 9. Explicitly out of scope

- Postprocessor design, macro templates, G-code generation, DNC, CNC connectivity and machine PLC control.
- SolidCAM VMID parser compatibility or importing entire SolidCAM settings.
- Browser-native STEP loading in the embedded viewer in this first increment.
- Automatic creation of a perfect kinematic hierarchy from a monolithic CAD assembly.
- Physically proven machine collision checking unless separately implemented and tested; machine-mesh participation metadata is in scope.
- Changing production scheduling policies or time estimation algorithms without separate tests and product decision.

## 10. Codex agent instruction

Implement this PRD in the repository as a sequence of reviewable vertical slices, preserving the existing application behavior. **Start with Slice A, not a wholesale rewrite.** Keep the schema and migration backward compatible; use established C# server, WPF client and Meimad NCViewer wrapper patterns. The server owns persisted model and assets. Do not modify vendored third_party. Report any material unresolved design conflict in docs/implementation-plan.md rather than inventing behavior, and do not claim feature complete unless acceptance criteria are tested. Always distinguish unverified physical machine geometry/calibration from correct abstract mathematics.

**This pull request is a requirements-only proposal. It does not contain an implementation of Machine Designer.**
