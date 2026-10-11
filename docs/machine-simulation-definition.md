# Machine simulation definition (schema v1, Server schema v114)

**Status:** implemented contract of the Machine Designer (PRD `docs/prd-machine-designer-mini-vmid.md`). Choices the
PRD left open are marked **Decision** and listed in `docs/implementation-plan.md`. Positions carried over from the
installed NC viewer machines are placeholders until someone verifies them on the real machine; the mathematics below
is exact, the physical numbers are not.

One Server-authoritative, versioned definition per physical Machine describes the machine as a tree of static and
moving components authored at the **all-axes-zero reference pose**: its physical joints, mounts, meshes, tool magazine
and timing. The Server stores a draft and a published copy and is the only writer. The NC viewer, the Machine Designer
and the Planner use the **published** copy; the NC viewer compiles it into the NC engine's machine format, so one
kinematic evaluator (the vendored `CncKinematics`) drives playback, and the designer's page uses the same formula
(`meimad-designer-core.js`, tested against the C# `MachineKinematics`).

Code: `shared/Meimad.Planner.NcEngine/MachineDefinitions/` (`MachineSimulationDefinition`, `MachineKinematics`,
`MachineSimulationValidator`, `MachineSimulationCompiler`).

## 1. JSON shape

```json
{
  "schemaVersion": 1,
  "machineId": "machine-10",
  "name": "Haas VF-3SS",
  "units": "mm",
  "angleUnits": "deg",
  "handedness": "right",
  "referencePose": "all-axes-zero",
  "template": "haas-vf-3ss",
  "interpreterAxes": { "A": "B" },
  "nodes": [
    { "id": "base", "parent": null, "label": "Base", "position": [0, 0, 0],
      "primitive": { "shape": "box", "size": [1500, 900, 60], "center": [-508, -254, -760] },
      "models": [ { "assetId": "3f0c…", "name": "Enclosure", "alignment": { "position": [0, 0, 0], "rotationDeg": [0, 0, 0], "scale": 1 },
                    "color": "#9AA4AE", "visible": true, "opacity": 0.35, "wireframe": false, "collisionParticipation": true } ] },
    { "id": "x-table", "parent": "y-saddle", "label": "X table", "position": [0, 0, 0],
      "joint": { "axis": "X", "type": "linear", "direction": [-1, 0, 0], "min": -1016, "max": 0, "rapidRate": 35600, "maxFeed": 21200 } },
    { "id": "c-platter", "parent": "a-trunnion", "label": "C platter", "position": [0, 0, 0],
      "joint": { "axis": "C", "type": "rotary", "point": [0, 0, 0], "direction": [0, 0, -1], "continuous": true, "rapidRate": 18000 } }
  ],
  "mounts": {
    "tool": { "node": "spindle", "position": [0, 0, 0], "rotationDeg": [0, 0, 0] },
    "workpiece": { "node": "x-table", "position": [-508, -254, -560], "rotationDeg": [0, 0, 0] }
  },
  "magazine": { "nominalCapacity": 30, "usablePositions": 24, "toolChangeSeconds": 4.2, "safePosition": null },
  "timing": { "rapidRateMillimetersPerMinute": 35600, "machineTimeFactor": 1.0 },
  "collision": { "ignoredPairs": [["x-table", "y-saddle"]] },
  "calibration": { "zeroVerified": false, "rotaryLinesVerified": false, "mountsVerified": false, "notes": "Placeholder table position." }
}
```

All lengths in mm, angles in degrees, right-handed machine coordinates. Numbers are invariant JSON numbers; null
members are omitted.

### Nodes
- `id`: stable, `^[a-z0-9][a-z0-9_-]{0,63}$`, unique. Exactly one root (`parent: null`), every parent exists, no
  cycles, at most 200 nodes.
- `position`: the node's zero-pose translation **in its parent's frame** (mm). **Decision:** node frames carry no
  rotation in schema v1, so every frame is parallel to the machine frame at zero. Rotated CAD parts are aligned per
  mesh (`alignment.rotationDeg`). This keeps the definition exactly representable by the NC engine's kinematics.
- `joint` (optional):
  - `axis`: physical axis name `^[A-Z][A-Z0-9]{0,3}$`, unique across joints. The readout shows this name.
  - `type`: `linear` or `rotary`.
  - `direction`: a finite, non-zero vector of positive physical motion in the parent frame, normalized on save.
  - `point`: **rotary only, required**. A point on the rotation line in the parent frame. A linear joint with a `point`
    is invalid. There is no separate pivot or MRZP field.
  - `min`/`max`: signed absolute readings relative to the reference zero (mm or deg).
    - `min <= max` is required.
    - `min <= 0 <= max` is also required unless `zeroOutsideReason` says why zero is outside the travel (Mazak
      Variaxis Z 150..660 carries such a reason from its installed definition).
    - A linear joint needs both limits.
    - A rotary joint needs both limits, or `continuous: true` (no limits; the reading wraps).
  - `rapidRate` (mm/min or deg/min, > 0) and `maxFeed` (> 0). **Decision:** a missing rapid rate is a warning
    (`rapid_rate_missing`), not an error, because several installed machines do not state one.
  - `scale` (default 1): physical displacement per unit of the reading. Use `0.5` for a lathe X read as a diameter
    (`diameter: true`).
- Kinematics in the parent frame:
  - linear: `T(position) · T(scale·q·d)`
  - rotary: `T(position) · T(p) · R(d, scale·q) · T(-p)`, with `p = point - position` and a right-hand rotation about `d`.

  Ancestors compose root first, and `q = 0` gives exactly the authored pose.
- `primitive` (optional): box or cylinder fallback geometry, drawn while the node has no loaded mesh.
  - Box: `shape`, `size`, `center` (node frame).
  - Cylinder: `shape`, `radius`, `height`, `axis`, `center`.
- `models`: up to 32 meshes per node. Each has:
  - `assetId` (32 hex characters, a Server asset of the same Machine), **or** `legacyPath`. A legacy path is a file
    path carried over from the schema v113 `ncViewerModels`.
    - **Decision:** a legacy path may only be kept, never added. It stays a warning until the mesh is imported into
      the asset store.
  - `name`.
  - `alignment`:
    - `position` and `rotationDeg` (X then Y then Z, extrinsic: R = Rz·Ry·Rx).
    - `scale` > 0. Use 25.4 for a model drawn in inches; GLB metres are converted on reading.
  - Display: `color` (`#RRGGBB`), `visible`, `opacity` (0..1), `wireframe`.
  - `collisionParticipation`. Visibility and opacity never change it.

### Mounts
`tool` and `workpiece` are frames on a node: `node`, `position` in that node's frame, and `rotationDeg`. Both are
required to publish a definition that has components (a definition without components, used for automatic detection,
publishes magazine and timing only).
- **Decision:** the NC engine assumes the tool along +Z of the tool mount, so a non-zero tool-mount rotation is an
  error.
- A workpiece-mount rotation is a warning (display only).

### Interpreter axes
`interpreterAxes` maps a physical axis to the axis name the NC engine interprets. On the Mazak Variaxis the physical
`A` is interpreted as `B`. The compiler renames joints for the engine and labels them with the physical name. The
viewer and the designer show physical names.

### Magazine and timing (single write path)
These definition fields are the only write path of the Machine's former columns, which become read-only projections:

| Definition field | Machine column |
|---|---|
| `magazine.usablePositions` | `usable_tool_positions` |
| `magazine.toolChangeSeconds` | `tool_change_time_seconds` |
| `timing.rapidRateMillimetersPerMinute` | `rapid_rate_mm_per_min` |
| `timing.machineTimeFactor` | `machine_time_factor` |
| `template` | `nc_viewer_machine` |
| models that still have a legacy path | `nc_viewer_models_json` |

- Publishing writes the projections in the same transaction and recalculates the Machine's NC cycle estimates when
  a projected value changed. `NcCycleTimeEstimator` is unchanged.
- Machine PATCH accepts the current value of these fields (older forms send the whole Machine). It refuses a
  different value with issue code `machine_definition_owned_field` (422 `validation_failed`).
- Machine create accepts them as the new Machine's first definition.
- Unknown values stay null.
- `0 < usablePositions <= nominalCapacity` when both are known.

### Calibration and collisions
`calibration` flags stay false until a person verifies the real machine; the designer marks unverified definitions.
`collision.ignoredPairs` lists node pairs. **No machine collision solver exists**: the NC viewer and the designer state
"Machine collisions not checked" and check axis travel only.

## 2. Validation

`MachineSimulationValidator` returns issues `{ path, code, message, severity }`. An `error` blocks publishing; a
`warning` does not.

Definition codes:

| Area | Codes |
|---|---|
| Schema and size | `schema_version`, `units`, `too_many_nodes` |
| Ids and tree | `invalid_id`, `duplicate_id`, `root_count`, `missing_parent`, `cycle` |
| Numbers | `not_finite`, `out_of_range` |
| Joints | `invalid_axis`, `duplicate_axis`, `invalid_joint_type`, `zero_direction`, `point_not_allowed`, `point_required` |
| Travel, speed and scale | `limits_order`, `zero_outside_limits`, `limits_required`, `invalid_scale`, `rapid_rate_missing` (warning) |
| Meshes | `too_many_models`, `asset_reference`, `invalid_color`, `legacy_path` (warning) |
| Mounts | `mount_required`, `mount_node`, `mount_rotation` |
| Magazine, timing, template, axes | `magazine_capacity`, `invalid_rate`, `template_required`, `interpreter_axes` |
| Collisions | `collision_pair` |
| Calibration | `unverified` (warning) |

The Server adds:
- `asset_missing`: an asset id that is not this Machine's asset.
- `legacy_path_not_allowed`: a legacy path that was in neither the draft nor the published copy. This refuses the save.
- `template_not_installed`.
- The projected Machine values' own validation codes (for example `out_of_range`), with paths such as
  `magazine.usablePositions`.

The designer adds `not_finite` for fields that are not numbers, and `mesh_not_drawn` / `asset_unavailable` for meshes
it could not draw.

## 3. Compilation for the NC engine

`MachineSimulationCompiler.Compile(definition, template)` builds an engine machine with id
`meimad-machine-<machineId>`. Controller, spindle, cycles, tool changer and work offsets come from the `template`, and
detection keywords are empty, so a compiled machine is only used when selected.
- `axes` come from the joints, renamed by `interpreterAxes`. A renamed axis carries `programmedAs` and `label` = the
  physical name.
- `kinematics.nodes` come from the nodes:
  - `origin` = `position` and `pivot` = `point − position`; `scale` and `primitive` (as `geometry`) are carried over.
  - Two fixed child nodes are added: `meimad-tool-mount` (`toolMount`, origin = the tool mount position) and
    `meimad-workpiece-mount` (`workpieceMount`, origin 0).
  - **Decision:** the engine reads machine coordinates as the tool gauge point relative to its all-axes-zero position,
    so the compiled root is shifted by minus the tool mount's zero-pose position.
- `mrzp` (when the template has one) is the workpiece mount at zero relative to the tool mount. `verified` follows
  `calibration.mountsVerified`.
- The tool changer's capacity (`capacity`, or `stations` for turrets) and `changeSeconds` come from the magazine when
  known.

A definition without nodes or template compiles to nothing, and the Machine keeps its template or auto-detection.

`MachineSimulationCompiler.FromTemplate(template, id, legacy)` builds a definition from an installed machine:
- Positions come from the template's origins. Directions are multiplied by the template's sense. Rotation points come
  from the template pivots, with `"mrzp"` and Setting 254 resolved as the engine resolves them.
- Limits come from the template's axes; a rotary axis without limits becomes continuous.
- Primitives come from the template geometry, mounts from its tool/workpiece mount nodes (the workpiece mount at the
  template MRZP), and `interpreterAxes` from axes declared `programmedAs`.
- Magazine and timing come from the Machine's values; the nominal capacity comes from the template's tool changer.
- `legacyPath` models come from schema v113 `ncViewerModels`. A model whose node does not exist keeps a fixed node of
  that id.

Tests verify the compiled engine chain of every installed machine equals the template's chain.

The engine registers compiled machines at runtime (`NcEngineRuntime.UseMachines`, served from an in-memory folder).
When one is rejected, none is registered, the installed machines keep working, and the viewer falls back to the
definition's template with a warning.

## 4. Server API

Every signed-in user reads; writes need `setup.manage`. The loopback-only Machines exemption for the upgrade script
does not cover these routes.

- `GET /api/v1/machines/{id}/simulation-definition` returns:
  - `machineId`, `draft`, `draftVersion`, `published`, `publishedVersion`, `publishedAt`, `publishedBy`, `updatedAt`,
    `updatedBy`;
  - `hasUnpublishedChanges`;
  - `issues` (the draft's, including the Server checks).

  ETag: `"machine-simulation:{id}:v{draftVersion}"`.
- `PUT …/simulation-definition` saves the draft. It needs `If-Match` with that tag and a body of at most 2 MB.
  - Issues are returned, not enforced, except that a new legacy path is refused (`422 machine_definition_invalid`).
  - Malformed JSON is `400`.
  - A missing tag is `428`. A stale tag is `412 resource_version_stale`, with the conflict's `changedBy`/`changedAt`.
- `POST …/simulation-definition/validate` returns `{ issues }` for the body, including the publish-time checks.
- `POST …/simulation-definition/publish` publishes the saved draft. It needs `If-Match` with the draft tag.
  - With errors it is `422 machine_definition_invalid`, with the issues in `details`.
  - Otherwise it copies the draft to published, increments `publishedVersion` and writes the projection columns. When a
    projected value changed, it also bumps the Machine `version` and recalculates the cycle estimates.
- `GET /api/v1/machines/simulation-definitions` returns `{ items: [{ machineId, publishedVersion, published }] }` for
  every Machine. The NC viewer uses it.
- `POST /api/v1/machines/{id}/simulation-assets?name=<file>` uploads a mesh as a raw body.
  - Limit: 200 MB.
  - Accepted: `.glb` with the glTF magic, version 2 and a matching length, or `.stl` (binary with a matching triangle
    count, or ASCII `solid`).
  - Returns `201` with `{ assetId, fileName, mediaType, sizeBytes, sha256, createdAt, createdBy }`.
  - Identical content is stored once per Machine; the same asset comes back.
  - Errors: `unsupported_asset_type`, `invalid_asset_content`, `asset_empty` (422) and `asset_too_large` (413).
- `GET …/simulation-assets` lists the Machine's assets. `GET …/simulation-assets/{assetId}/content` streams one, with
  `model/gltf-binary` or `model/stl` and an ETag of its SHA-256.

Assets are stored below the G-code release root (`machine-assets/<machineId>/<sha256>.<ext>`), never at client paths,
so the recovery set backs them up and restores them with the releases.

## 5. Migration (schema v114)

Every existing Machine gets a definition built by `FromTemplate`:
- from its `nc_viewer_machine`;
- with its `nc_viewer_models_json` as `legacyPath` models, kept in network-folder storage form and served resolved;
- with its magazine and timing columns.

Draft and published copies are equal (version 1), so the viewer, the Planner and NC analysis behave as before; the
Machine rows are unchanged.
- A Machine without an NC viewer machine gets a definition with no nodes (auto-detection).
- A Machine created later gets its first definition in the same transaction.
- A Machine somehow without a row gets one when it is first read.

Definitions and assets are not planning inputs. Their triggers advance the all-input revision and the non-planning
counter. A publish that changes a projected column changes the Machine row, which moves the Machine's own revision.

## 6. Clients

**Machine Designer** (Setup → Machines → *Open Machine Designer…*):
- Layout:
  - the component tree;
  - a WebView2 3D view (`machine-designer.html`) with orbit, pick, fit, mesh and primitive toggles, mount markers and
    the selected joint's line;
  - jog sliders per physical axis within its travel, and *Zero all*;
  - tabs: General (template, interpreter axes, mounts, calibration, collision pairs), Component (position, joint,
    primitive), Geometry (meshes), Timing & Tool Magazine, and Validation.
- Actions: Save draft, Validate, Publish (asks first) and Reload.
- Mesh import:
  - GLB and STL are uploaded as they are; STEP is tessellated on the PC and uploaded as STL.
  - A legacy path can be stored on the Server.
- *Start from this installed machine* replaces the components with `FromTemplate` and keeps magazine and timing.
- Without `setup.manage` the definition is read-only, but jogging still works.
- Meshes that cannot be drawn are listed as errors, and their component keeps its primitive.

**Setup → Machines** no longer edits the NC viewer machine, magazine, timing or 3D models. It shows them read-only
with the button, sends the current values unchanged on update, and creates new Machines without them.

**NC viewer:**
- Loads the published definitions and compiles them against its own installed templates.
- Registers the compiled machines with its engine and selects the compiled machine for the Operation's Machine.
- Draws the definition's meshes on their nodes, with color, opacity, wireframe and scale. Uploaded meshes come from a
  local hash-checked cache; GLB is converted to STL on the PC.
- The Axes card shows physical axis names and "Machine collisions not checked".

Server NC analysis is machine-independent (feed time, rapid distance, tool changes, dwell; the Machine's timing values
are applied afterwards) and keeps interpreting with the template, the `nc_viewer_machine` projection.
**Decision:** per-Machine kinematics do not change the stored analysis.
