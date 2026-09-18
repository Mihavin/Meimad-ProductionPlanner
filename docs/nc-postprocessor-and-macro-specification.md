# NC postprocessor and Production Package specification

**Revision:** 1.9 (2026-09-18)
**Active protocol:** canonical Production Package protocol v2
**Controllers covered:** Haas NGC; FANUC 0i-D/0i-F and 30i/31i/32i-B; Mazak Matrix / Matrix 2 (EIA); Okuma OSP-P200/P300 (User Task 2)
**Audience:** SolidCAM/Cimatron postprocessor writers and CNC controls reviewers
**Supersedes:** the Haas-only "Haas NGC postprocessor and Production Package specification" and Word guides v1.7 and v1.8

The postprocessor is server-blind. It writes normal deterministic cutting code for
its control and exact Meimad placeholders. It does not contact the Planner, select a
Machine, create a package, assign an NC identity, or copy Part/Operation names from
CAM fields into Meimad metadata.

The authoritative contract is
[`postprocessor-production-package-contract.md`](postprocessor-production-package-contract.md).
The placeholder grammar is the same for every control. What differs per control is the
surrounding syntax the writer emits (comments, program header, output statements) and
the syntax the Server injects, which follows the **NC dialect** configured on the Machine
in Setup: `HAAS_NGC`, `FANUC_MACRO_B`, `MAZAK_MATRIX_EIA`, or `OKUMA_OSP` (section 4).

**Warning about the draft:** a newer draft contract (adding a `POSTPROCESSOR_ID` token and
other changes) exists at
[`haas-ngc-postprocessor-and-macro-specification.v2-draft.md`](haas-ngc-postprocessor-and-macro-specification.v2-draft.md).
That draft is **not implemented** by the current Production Package Creator. Do not write
a postprocessor against it. `POSTPROCESSOR_ID` is not a recognized placeholder key; using it
fails Production Package creation with `production_package_placeholder_unknown`. Use this
document only.

## 1. Required canonical block

Every new CNC source template must contain this logical structure. Comment wording
around a token may vary, but the token spelling may not, and the part-counting markers
are part of the block, not an extra.

```gcode
%
O1500
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])
(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
(MACHINE: [[MEIMAD:MACHINE_ID]])
(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])
(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])
[[MEIMAD:VERIFICATION_HOOK]]
[[MEIMAD:EVENT_CONTEXT]]
DPRNT[[[MEIMAD:PART_NAME]]]
[[MEIMAD:CYCLE_START]]
G90 G17 G40 G49 G80
(normal CAM-generated tools, motion, feeds, speeds and cycles for one physical part)
[[MEIMAD:CYCLE_END]]
M30
%
```

| Key | Occurrences | Where it goes |
|---|---|---|
| `PART_NAME` | one or more | a `( )` header comment **and** the bare print line of section 2 |
| `OPERATION_NAME` | one or more | `( )` comments only, never inside a print statement |
| `PRODUCTION_RUN_ID` | exactly one | `( )` comment |
| `PRODUCTION_PACKAGE_ID` | exactly one | `( )` comment |
| `MACHINE_ID` | exactly one | `( )` comment |
| `NC_RELEASE_ID` | exactly one | `( )` comment |
| `OFFSET_LOADER_RELEASE_ID` | exactly one | `( )` comment |
| `VERIFICATION_HOOK` | exactly one | its own line, before the first executable block |
| `EVENT_CONTEXT` | exactly one | its own line, after the hook (after `POPEN` on FANUC) |
| `CYCLE_START` | exactly one | its own line, immediately before the first block of the physical cycle |
| `CYCLE_END` | exactly one | its own line, after the last block of the cycle on the successful path, before `M30`/`M02` |

The hook must be before the first executable NC block; a `DPRNT`, `PUT`, `POPEN`, `G`,
`M`, `T`, or `S` word is executable, a `%`, an `O` program header, an empty line, a
full-line `( )` comment, or a placeholder line is not.

Do not emit a real Part name, Operation name, Planner Machine ID, Run ID,
Package ID, release ID, user, timestamp, challenge, response, or verification
call. The Server owns all of those values.

`MACHINE_ID`, `NC_RELEASE_ID`, `RUN_ID` (via `PRODUCTION_RUN_ID`), and `PACKAGE_ID` (via
`PRODUCTION_PACKAGE_ID`) resolve to short unique 6-digit numbers meant to be read and typed
by hand at the control (e.g. `483921`), not to the Server's internal identifiers. Do not
assume these resolved values are the same length or format as any internal ID string.

## 2. The PartName output line

The Server takes machine-side Part identity from one bare output line that contains only
the Part number, emitted by the runnable NC at program start. On Haas, FANUC, and Mazak it
is a DPRNT; on Okuma it is a `PUT` flushed by `WRITE C`:

```gcode
DPRNT[[[MEIMAD:PART_NAME]]]
```

```gcode
PUT '[[MEIMAD:PART_NAME]]'
WRITE C
```

After package resolution the control prints, for example, `30P647004101-001`, and the
Server accepts the line because it is **part-number-shaped**: uppercase letters and
digits, at least one digit, at least one `-` or `.` separator between groups, and not
ending in `.CNC`. Everything else on the DPRNT channel is ignored for Part identity.

| Printed line | Accepted as Part identity | Why |
|---|---|---|
| `30P647004101-001` | yes | letters/digits, a `-` separator, has digits |
| `4711.02` | yes | digits with a `.` separator |
| `PART=30P647004101-001` | no | `=` is not allowed; the whole line is ignored |
| `ABC123` | no | no `-` or `.` separator |
| `AB_12-1` | no | `_` is not allowed |
| `30p647-1` | no | lowercase (FANUC-family DPRNT would not print it anyway) |
| `O1500.CNC` | no | ends in `.CNC` (file names are never Part numbers) |
| `MEIMAD/V/2/CONTEXT/...` | no (event line) | handled by event ingestion, not Part identity |

- `DPRNT[PART=[[MEIMAD:PART_NAME]]]` is **wrong**: the control prints `PART=30P647...`,
  which is not part-number-shaped and is silently ignored. Earlier versions of this
  document and of the contract showed that form; it never matched.
- Do not put `OPERATION_NAME` or any other value inside a print statement. Operation
  names may contain spaces and lowercase letters, which DPRNT on FANUC-family controls
  does not output reliably and which the Server would not read anyway. Keep them in
  `( )` comments.
- Part numbers that the print line must carry are therefore limited to `A-Z`, `0-9`, `-`,
  and `.`; a Part number such as `ABC123` (no separator) or `AB_12` (underscore) is never
  recognized on this path and would be identified only through the optional NC header
  reader.
- The Server strips ASCII control codes from every line before parsing, so the DC2 and
  DC4 codes that FANUC emits with `POPEN`/`PCLOS`, and NUL padding from a serial bridge,
  do not corrupt the line.

The line is executable, so it comes after `VERIFICATION_HOOK`. On FANUC-family controls
it must also come after `POPEN` (section 6.2).

## 3. Part counting: the cycle markers are required

The Planner counts produced parts from two printed events per physical cycle: `CST`
(cycle start) and `CEN` (cycle end). The postprocessor marks where they belong:

```gcode
[[MEIMAD:CYCLE_START]]
(normal CAM-generated tools, motion, feeds, speeds and cycles for one complete
physical part cycle)
[[MEIMAD:CYCLE_END]]
```

Every new template contains exactly one `CYCLE_START` and exactly one `CYCLE_END`, each
on its own line, `CYCLE_START` first. This is not optional: an Operation whose template
has no pair produces no part count, no cycle timing, and no interrupted-cycle evidence,
and the Run has to be counted by hand. The Server rejects a template that has one marker
without the other (`production_package_cycle_marker_unpaired`) or the pair out of order
(`production_package_cycle_marker_order_invalid`), and it refuses to build a package from a
template that has neither marker (`production_package_cycle_marker_required`). The check
runs when a package is built, so an older release without the pair fails at its next
package build and has to be re-released with the pair.

Package Creator expands the pair into the wire-format part-counting events in the
Machine's dialect (section 5) only when the assigned Machine has **Server Verification
enabled**. On a verification-disabled Machine both markers are silently removed, nothing
is printed, and nothing is counted, but the template stays valid; when verification is
enabled later the same released NC starts counting without being re-posted. There is no
non-verification path for automated part counting.

### 3.1 Placement rules

- `CYCLE_START` goes immediately before the work that begins one physical cycle: in a CAM
  program that machines one part per run, right after the header block, before the first
  `G`/`T`/`S` word.
- `CYCLE_END` goes only on the common successful path after that cycle has fully
  completed: after the last cut, after any in-program probing or deburring that belongs to
  the part, before `M30`/`M99`/`M02` or any successful return.
- Never on an alarm, reset, optional-stop, or failure path. An `M00`/`M01` between tools
  is fine inside the pair; a branch that skips the rest of the part after an `M00` must
  not reach `CYCLE_END`.
- One pair per template. Do not wrap every tool, CAM procedure, or subprogram; do not put
  a marker in a subprogram that is called more than once. The pair belongs to the main
  program around the atomic cycle the Server should count.
- One pair means one counted cycle per program run. If a program machines two fixtures in
  one run, the Server still counts one cycle per run; the events carry no per-part
  multiplier.

### 3.2 Placement examples

Single part, tools in sequence (the normal CAM case):

```gcode
DPRNT[[[MEIMAD:PART_NAME]]]
[[MEIMAD:CYCLE_START]]
G90 G17 G40 G49 G80
T1 M06
(roughing)
T2 M06
(finishing)
M01
T3 M06
(drilling)
[[MEIMAD:CYCLE_END]]
M30
```

Main program calling subprograms (`M98`): the pair stays in the main program, the
subprograms carry no Meimad tokens at all.

```gcode
DPRNT[[[MEIMAD:PART_NAME]]]
[[MEIMAD:CYCLE_START]]
G90 G54
M98 P2001 (SIDE A)
G55
M98 P2001 (SIDE B)
[[MEIMAD:CYCLE_END]]
M30
```

In-program measurement that belongs to the part: `CYCLE_END` comes after it, because the
part is not finished until the probe cycle has run.

```gcode
(finish milling)
G65 P9810 Z50. (PROBE RETRACT)
G65 P9814 D30. (BORE MEASURE)
[[MEIMAD:CYCLE_END]]
M30
```

Wrong: a marker per tool (duplicate markers are rejected) or `CYCLE_END` before an
optional stop that may end the program early.

```gcode
T1 M06
[[MEIMAD:CYCLE_START]]   <- rejected: production_package_placeholder_duplicate
(roughing)
[[MEIMAD:CYCLE_END]]
T2 M06
[[MEIMAD:CYCLE_START]]
(finishing)
[[MEIMAD:CYCLE_END]]
M30
```

## 4. NC dialects: what the Server generates for each control

Every Machine in Setup has an **NC dialect**. It selects the syntax of everything the
Server injects and the variable numbers the Machine's verification configuration accepts.
The template in section 1 does not change with the dialect.

| Injected element | HAAS_NGC | FANUC_MACRO_B and MAZAK_MATRIX_EIA | OKUMA_OSP |
|---|---|---|---|
| Verification hook | `G65 P9002 A654321. (MEIMAD VERIFY V1)` | same as Haas | `CALL O9002 PA=654321 (MEIMAD VERIFY V1)` |
| Event context | `(MEIMAD EVENT CONTEXT V2)` then `DPRNT[MEIMAD/V/2/CONTEXT/...]` | same as Haas | `(MEIMAD EVENT CONTEXT V2)` then `PUT 'MEIMAD/V/2/CONTEXT/...'` and `WRITE C` |
| Cycle block variables | scratch `#30`, sequence `#10504` (configured), timer `#3001` | scratch `#30`, sequence `#504` (configured), timer `#3001` | sequence `VC5` (configured); the event ID uses the sequence instead of a timer |
| Look-ahead barrier | `G103 P1` before and `G103 P0` after the block | none (register a non-buffered M-code on the control if event order matters) | none |
| Branching in the cycle block | `IF [...] THEN` | `IF [...] THEN` | `IF [...] NMDS1` / `GOTO NMDS2` with sequence names `NMDS1`, `NMDS2`, `NMDE1`, `NMDE2` |
| Event print | `DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-654321-S-#3001[80]/SEQ/#30[60]/...]` | same as Haas | `PUT 'MEIMAD/V/1/EVENT/CST/ID/NC-654321-S-'`, `PUT VC5,6,0`, `PUT '/SEQ/'`, `PUT VC5,6,0`, `PUT '/MACROVERSION/6/PROGRAM/654321'`, `WRITE C` |
| Offset Loader artifact | `offset-loader/O01990.nc`: `%`, `O01990 (...)`, comments, `G65 P9001 A<release>. B654321.`, `M30`, `%` | same as Haas | `offset-loader/O1990.MIN`: comments, `CALL O9001 PA=<release> PB=654321`, `M02` (no `O` line, no `%`) |
| Verification variables (nonce, state, release token, sequence) | `#10000-#10999` | `#500-#999` | `VC1-VC200` (enter the number only) |
| Response variable | Haas M109 `#500-#549` or `#10500-#10549` | `#500-#999` | `VC1-VC200` |

Which dialect a Machine in this factory needs:

| Machine | Control | NC dialect | Connection type in Setup | DPRNT source |
|---|---|---|---|---|
| Haas VF-3SS, UMC-500SS, ST-25Y | Haas NGC | `HAAS_NGC` | Haas MDC or Haas MTConnect | TCP (Setting 263 port) |
| FANUC-controlled mills | 0i-D/0i-F, 30i/31i/32i-B | `FANUC_MACRO_B` | FANUC FOCAS | TCP via serial-to-Ethernet bridge, or FILE |
| Mazak Variaxis i-500 | Matrix 2 (EIA) | `MAZAK_MATRIX_EIA` | DPRNT only | FILE `\\192.168.0.158\print\print.txt` |
| Okuma Genos L200E-M | OSP-P200L | `OKUMA_OSP` | DPRNT only | TCP via bridge, or FILE |

Rules that follow from the tables:

- The event context block is emitted for verification-enabled and verification-disabled
  Machines alike; the hook and the cycle blocks only when verification is enabled.
- Changing a Machine's dialect is refused while its Server Verification is enabled,
  because the stored variable numbers belong to the old control. Disable, change the
  dialect, re-enter the mappings, re-enable.
- Legacy `(MEIMAD PACKAGE ... V1)` releases can only be built for `HAAS_NGC`; a Machine
  with another dialect rejects them with `production_package_dialect_legacy_unsupported`.
  Re-release the NC as a canonical template.
- The Server strips all whitespace from a `MEIMAD/` line before parsing it, so numeric
  fields printed with leading blanks (FANUC with parameter 6001#1 = 0, Okuma `PUT VC5,6,0`)
  are read correctly.
- Every dialect other than Haas NGC has been implemented from the control makers'
  documented syntax and is covered by Server tests, but has not yet run on a physical
  control in this factory. Commission it exactly like the Haas macros: bounded no-motion
  test first, verification disabled until the challenge/verify/finalizer subprograms for
  that control exist and pass.

## 5. Generated runnable NC examples

Given the template in section 1, NC identity `654321`, Run `483002`, Package `483001`,
macro version `6`, verify program `O9002`, Server Verification enabled, and the cycle
markers around one cycle. The text the control receives is exactly what is shown; the
only writer-controlled parts are the header comments and the cutting code.

### 5.1 HAAS_NGC (Machine `10`, event-sequence variable `#10504`)

```gcode
%
O1500
(PART: 30P647004101-001)
(OPERATION: OP20 MILL 5AX)
(RUN: 483002)
(PACKAGE: 483001)
(MACHINE: 10)
(NC RELEASE: 654321)
(OFFSET LOADER: <offset-loader-release-id>)
G65 P9002 A654321. (MEIMAD VERIFY V1)
(MEIMAD EVENT CONTEXT V2)
DPRNT[MEIMAD/V/2/CONTEXT/PACKAGE/483001/RUN/483002/MACHINE/10/NCRELEASE/654321/MACROVERSION/6/PROGRAM/654321]
DPRNT[30P647004101-001]
G103 P1
#30=ROUND[#10504]
IF [ABS[#10504-#30] GT 0.0001] THEN #30=0.
IF [#30 LT 0.] THEN #30=0.
IF [#30 GE 899999.] THEN #30=0.
#30=#30+1.
#10504=#30
DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-654321-S-#3001[80]/SEQ/#30[60]/MACROVERSION/6/PROGRAM/654321]
G103 P0
G90 G17 G40 G49 G80
(one complete physical cycle)
G103 P1
#30=ROUND[#10504]
IF [ABS[#10504-#30] GT 0.0001] THEN #30=0.
IF [#30 LT 0.] THEN #30=0.
IF [#30 GE 899999.] THEN #30=0.
#30=#30+1.
#10504=#30
DPRNT[MEIMAD/V/1/EVENT/CEN/ID/NC-654321-E-#3001[80]/SEQ/#30[60]/MACROVERSION/6/PROGRAM/654321]
G103 P0
M30
%
```

The generated Haas Offset Loader (`offset-loader/O01990.nc`) for the same package:

```gcode
%
O01990 (MEIMAD PACKAGE OFFSET LOADER)
(PRODUCTION PACKAGE 12)
(PRODUCTION RUN 483002)
(BATCH OPERATION <id>)
(MACHINE 10)
(NC RELEASE 654321)
(OFFSET LOADER RELEASE <id>)
(MEASURED TOOL OFFSETS - VERIFICATION AND RELEASE BINDING)
G65 P9001 A483920. B654321.
M30
%
```

### 5.2 FANUC_MACRO_B (Machine `11`, event-sequence variable `#504`)

From the FANUC template of section 6.2, which adds `POPEN`/`PCLOS`:

```gcode
%
O1500
(PART: 30P647004101-001)
(OPERATION: OP20 MILL 5AX)
(RUN: 483002)
(PACKAGE: 483001)
(MACHINE: 11)
(NC RELEASE: 654321)
(OFFSET LOADER: <offset-loader-release-id>)
G65 P9002 A654321. (MEIMAD VERIFY V1)
POPEN
(MEIMAD EVENT CONTEXT V2)
DPRNT[MEIMAD/V/2/CONTEXT/PACKAGE/483001/RUN/483002/MACHINE/11/NCRELEASE/654321/MACROVERSION/6/PROGRAM/654321]
DPRNT[30P647004101-001]
#30=ROUND[#504]
IF [ABS[#504-#30] GT 0.0001] THEN #30=0.
IF [#30 LT 0.] THEN #30=0.
IF [#30 GE 899999.] THEN #30=0.
#30=#30+1.
#504=#30
DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-654321-S-#3001[80]/SEQ/#30[60]/MACROVERSION/6/PROGRAM/654321]
G90 G17 G40 G49 G80
(one complete physical cycle)
#30=ROUND[#504]
IF [ABS[#504-#30] GT 0.0001] THEN #30=0.
IF [#30 LT 0.] THEN #30=0.
IF [#30 GE 899999.] THEN #30=0.
#30=#30+1.
#504=#30
DPRNT[MEIMAD/V/1/EVENT/CEN/ID/NC-654321-E-#3001[80]/SEQ/#30[60]/MACROVERSION/6/PROGRAM/654321]
PCLOS
M30
%
```

The Offset Loader is the same `O01990.nc` program as for Haas (section 5.1). Note that
nothing but the two `G103` lines differs from the Haas output; the `#504` sequence
variable comes from the Machine's verification configuration.

### 5.3 MAZAK_MATRIX_EIA (Machine `12`)

Identical to the FANUC output of section 5.2 apart from the header values, without the
`POPEN`/`PCLOS` lines if the template does not emit them. The DPRNT lines land in
`print.txt` on the control (section 6.3) and the Server tails that file.

### 5.4 OKUMA_OSP (Machine `13`, event-sequence variable `VC5`)

```gcode
(PART: 30P647004101-001)
(OPERATION: OP20 TURN)
(RUN: 483002)
(PACKAGE: 483001)
(MACHINE: 13)
(NC RELEASE: 654321)
(OFFSET LOADER: <offset-loader-release-id>)
CALL O9002 PA=654321 (MEIMAD VERIFY V1)
(MEIMAD EVENT CONTEXT V2)
PUT 'MEIMAD/V/2/CONTEXT/PACKAGE/483001/RUN/483002/MACHINE/13/NCRELEASE/654321/MACROVERSION/6/PROGRAM/654321'
WRITE C
PUT '30P647004101-001'
WRITE C
VC5=ROUND[VC5]
IF [VC5 LT 0] NMDS1
IF [VC5 GE 899999] NMDS1
GOTO NMDS2
NMDS1 VC5=0
NMDS2 VC5=VC5+1
PUT 'MEIMAD/V/1/EVENT/CST/ID/NC-654321-S-'
PUT VC5,6,0
PUT '/SEQ/'
PUT VC5,6,0
PUT '/MACROVERSION/6/PROGRAM/654321'
WRITE C
G15 H1
(one complete physical cycle)
VC5=ROUND[VC5]
IF [VC5 LT 0] NMDE1
IF [VC5 GE 899999] NMDE1
GOTO NMDE2
NMDE1 VC5=0
NMDE2 VC5=VC5+1
PUT 'MEIMAD/V/1/EVENT/CEN/ID/NC-654321-E-'
PUT VC5,6,0
PUT '/SEQ/'
PUT VC5,6,0
PUT '/MACROVERSION/6/PROGRAM/654321'
WRITE C
M02
```

The generated Okuma Offset Loader (`offset-loader/O1990.MIN`):

```gcode
(MEIMAD PACKAGE OFFSET LOADER)
(PRODUCTION PACKAGE 12)
(PRODUCTION RUN 483002)
(BATCH OPERATION <id>)
(MACHINE 13)
(NC RELEASE 654321)
(OFFSET LOADER RELEASE <id>)
(MEASURED TOOL OFFSETS - VERIFICATION AND RELEASE BINDING)
CALL O9001 PA=483920 PB=654321
M02
```

### 5.5 The same template on a verification-disabled Machine

Whatever the dialect, the hook line and both cycle blocks disappear, the Offset Loader
comment resolves to `NOT_APPLICABLE`, and the context line is still printed:

```gcode
%
O1500
(PART: 30P647004101-001)
(OPERATION: OP20 MILL 5AX)
(RUN: 483002)
(PACKAGE: 483001)
(MACHINE: 10)
(NC RELEASE: 654321)
(OFFSET LOADER: NOT_APPLICABLE)
(MEIMAD EVENT CONTEXT V2)
DPRNT[MEIMAD/V/2/CONTEXT/PACKAGE/483001/RUN/483002/MACHINE/10/NCRELEASE/654321/MACROVERSION/6/PROGRAM/654321]
DPRNT[30P647004101-001]
G90 G17 G40 G49 G80
(one complete physical cycle)
M30
%
```

The `MEIMAD/V/2/CONTEXT/...` line is correlation evidence in the raw DPRNT log; the Server's
event ingestion only parses `MEIMAD/V/1/EVENT/...` lines and records the context line as an
unsupported-version line. It is harmless and informational.

## 6. Controller templates and setup

The canonical block does not change between controls. Each example shows the complete
source template the postprocessor must produce for that control, the control setup that
routes the printed lines to the Server, and the Meimad settings that receive them.

### 6.1 Haas NGC (VF-3SS, UMC-500SS, ST-25Y): dialect HAAS_NGC

Haas NGC does not require `POPEN`/`PCLOS`. Mill template:

```gcode
%
O1500
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])
(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
(MACHINE: [[MEIMAD:MACHINE_ID]])
(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])
(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])
[[MEIMAD:VERIFICATION_HOOK]]
[[MEIMAD:EVENT_CONTEXT]]
DPRNT[[[MEIMAD:PART_NAME]]]
[[MEIMAD:CYCLE_START]]
G90 G17 G40 G49 G80
G54
T1 M06
S8000 M03
G43 H1 Z50.
(CAM toolpaths)
G53 G49 Z0.
[[MEIMAD:CYCLE_END]]
M30
%
```

The ST-25Y lathe template is the same block with lathe modal codes (`G18 G40 G80`,
`G50 S3000`, `G96`, `G97`) in the cutting section; the Meimad lines are identical.

Control setup:

- Setting 261 (DPRNT Destination) = `TCP Port`; Setting 263 (DPRNT TCP Port) = the port
  entered in Meimad (default `8080`). Alternatively Setting 261 = `Disk` with Setting 262
  naming a file under a shared User Data folder, which Meimad reads as a FILE source.
- The Server is the TCP client; the control accepts one read-only connection.

Meimad: Machine NC dialect `HAAS_NGC`; Connection type **Haas MDC** or **Haas MTConnect**;
DPRNT source `TCP` (controller IP, Setting 263 port) or `FILE`. Verification variables
`#10500`-range as today. Verification, Offset Loader, and part counting are implemented for
this control and remain bench-commissioning gated (see `cnc-commissioning-checklist.md`).

### 6.2 FANUC 0i-D / 0i-F and 30i / 31i / 32i-B: dialect FANUC_MACRO_B

FANUC requires `POPEN` before any DPRNT and `PCLOS` after the last one. `POPEN` is
executable, so it follows the hook; keep one `POPEN` at the start and one `PCLOS` before
`M30` so every DPRNT in between, including the Server-injected ones, is inside the open
channel.

```gcode
%
O1500
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])
(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
(MACHINE: [[MEIMAD:MACHINE_ID]])
(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])
(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])
[[MEIMAD:VERIFICATION_HOOK]]
POPEN
[[MEIMAD:EVENT_CONTEXT]]
DPRNT[[[MEIMAD:PART_NAME]]]
[[MEIMAD:CYCLE_START]]
G90 G17 G40 G49 G80
G54
T1 M06
S8000 M03
G43 H1 Z50.
(CAM toolpaths)
G91 G28 Z0.
[[MEIMAD:CYCLE_END]]
PCLOS
M30
%
```

`POPEN` sends the DC2 control code and `PCLOS` sends DC4 on the serial line; the Server
strips both before parsing, so the first and last lines are read correctly.

Control setup (typical values; confirm each number against the parameter manual of the
specific control and option set):

| Parameter | Purpose | Typical value |
|---|---|---|
| No. 0000 bit 1 (ISO) | Output code for DPRNT | 1 = ISO (ASCII) |
| No. 0020 (I/O CHANNEL) | Device used by DPRNT | 0 or 1 = RS-232-C port 1 (JD36A); 2 = port 2 (JD36B) |
| No. 0101 / 0111 | Stop bits and NUL handling for channel 1 / 2 | per bridge settings |
| No. 0102 / 0112 | Device type for channel 1 / 2 | the entry defined as "user"/generic RS-232 device |
| No. 0103 / 0113 | Baud rate code for channel 1 / 2 | e.g. 11 = 9600, 12 = 19200 |
| No. 6001 bit 1 (PRT) | Space output in DPRNT text | either; the Server removes blanks from event lines |
| No. 6001 bit 4 (CRO) | End-of-block after DPRNT | 0 = LF only or 1 = LF + CR; the Server accepts both |
| No. 3411–3420 | M-codes that stop look-ahead buffering | optional: place one before the cycle blocks if event timing must match motion |

The RS-232 port is wired to a serial-to-Ethernet bridge configured as a **TCP server**
with matching baud rate, data bits, parity, and stop bits. If the control carries a Data
Server / memory-card DPRNT destination instead (parameter 20 = 4, 5, or 17), the output
lands in a file on the control, not on the network; use it only where that file is
reachable as a UNC path, then configure the FILE source.

Meimad: Machine NC dialect `FANUC_MACRO_B`; Connection type **FANUC FOCAS** (controller IP,
MAC, FOCAS port 8193, part counter parameter 6711 or 6712); DPRNT source `TCP` with
**DPRNT TCP host** = the bridge's IP and **DPRNT TCP port** = its listening port, or
`FILE` for a shared print file, or `NONE` for monitoring only. Verification variables in
`#500-#999` (for example nonce `#501`, response `#505`, state `#502`, release token `#503`,
sequence `#504`). The generated hook and cycle blocks are valid custom macro B; the
protected O9001–O9003 subprograms and the operator response entry must be written and
commissioned for FANUC before verification is enabled, so keep it disabled until then.

### 6.3 Mazak Matrix / Matrix 2 (EIA/ISO programs): dialect MAZAK_MATRIX_EIA

Matrix executes custom-macro-style `DPRNT[...]`, `G65`, and `#` variables in EIA mode.
`POPEN`/`PCLOS` may be present or absent; whether the DPR14 file device needs them is
confirmed during commissioning, and the Server tolerates the DC2/DC4 they would add.

```gcode
%
O1500
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])
(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
(MACHINE: [[MEIMAD:MACHINE_ID]])
(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])
(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])
[[MEIMAD:VERIFICATION_HOOK]]
[[MEIMAD:EVENT_CONTEXT]]
DPRNT[[[MEIMAD:PART_NAME]]]
[[MEIMAD:CYCLE_START]]
G90 G17 G40 G49 G80
G54.1 P1
T1 M06
S8000 M03
G43 H1 Z50.
(CAM toolpaths, including the tilted-plane blocks the Variaxis post already writes)
G91 G28 Z0.
[[MEIMAD:CYCLE_END]]
M30
%
```

Control setup:

- Parameter DPR14 = 4 routes DPRNT output to the file `C:\MC_sdg\print\print.txt` on the
  control PC. Share that folder (for example as `print`) read/write for the Server service
  account; the Mazak Variaxis i-500 Matrix 2 in this factory is read at
  `\\192.168.0.158\print\print.txt`.
- No G-code deletes the file. The Server empties it according to the **Empty DPRNT file**
  policy; `ON_OFFSET_LOADER` keeps exactly one setup session in the file.

Meimad: Machine NC dialect `MAZAK_MATRIX_EIA`; Connection type **DPRNT only** (no
MDC/MTConnect/FOCAS telemetry on this control); DPRNT source `FILE`, file path as above,
clear policy `ON_OFFSET_LOADER`; verification variables in `#500-#999` as for FANUC. The
generated syntax is the FANUC macro B form without `G103`; the protected subprograms must
be ported and commissioned on the Matrix before verification is enabled.

Mazak Smooth (SmoothX / SmoothAi) controls: the DPRNT-to-file parameter differs from
DPR14 and has not been verified in this project; treat the Smooth Variaxis as "DPRNT only,
FILE source, path to be confirmed on the control".

### 6.4 Okuma OSP-P200 / P300: dialect OKUMA_OSP

Okuma OSP has no `DPRNT`, no `#` variables, and no `G65`; User Task 2 prints with `PUT`
(text in single quotes, or a variable with `digits,decimals`) and flushes a line with
`WRITE C`, calls subprograms with `CALL Onnnn PA=... PB=...`, keeps common variables in
`VC1-VC200`, and branches with `IF [...] Nname` / `GOTO Nname`. The Server injects exactly
that (section 4, section 5.4). Lathe template for the Genos L200E-M:

```gcode
(PART: [[MEIMAD:PART_NAME]])
(OPERATION: [[MEIMAD:OPERATION_NAME]])
(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])
(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
(MACHINE: [[MEIMAD:MACHINE_ID]])
(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])
(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])
[[MEIMAD:VERIFICATION_HOOK]]
[[MEIMAD:EVENT_CONTEXT]]
PUT '[[MEIMAD:PART_NAME]]'
WRITE C
[[MEIMAD:CYCLE_START]]
G15 H1
G50 S3000
G96 S200 M03
T010101
(CAM toolpaths)
G00 X300. Z200.
[[MEIMAD:CYCLE_END]]
M02
```

Rules for this template:

- `( )` comments are valid OSP comments, so the header block resolves unchanged.
- Release the program body without the `$NAME.MIN%` transfer header line: the Server's
  validator treats that line as the first executable block, which puts it before
  `VERIFICATION_HOOK` and fails the release with `verification_placeholder_not_first`.
- Do not start the file with an `O` name line; in an OSP `.MIN` file that turns the
  following blocks into a subprogram. The generated Offset Loader follows the same rule.
- The `PUT`/`WRITE` output device (the RS-232 port, or a print file on the OSP-P Windows
  side) is selected in the OSP parameters. Route it to a serial-to-Ethernet bridge (Meimad
  DPRNT source `TCP`, DPRNT TCP host = the bridge) or to a file on a shared folder (DPRNT
  source `FILE`). Confirm on the control which statement spelling and device the installed
  OSP version offers; the Server's OSP output is centralized in one place
  (`OkumaOspDialect`) so a spelling correction is a one-line change.
- The protected subprograms `O9001`–`O9003` (challenge, verify, finalizer) do not exist
  for OSP yet; they are written and commissioned on the Genos L200E-M before verification
  is enabled. The Server side is complete: hook, event context, cycle blocks, Offset Loader
  (`O1990.MIN`), and `VC1-VC200` variable validation.

Meimad: Machine NC dialect `OKUMA_OSP`; Connection type **DPRNT only** (there is no Okuma
telemetry adapter yet); DPRNT source `TCP` via bridge or `FILE`; verification variables as
plain numbers 1–200 (for example nonce `1`, response `2`, state `3`, release token `4`,
sequence `5`), which the Server renders as `VC1`…`VC5`.

## 7. SolidCAM GPPL examples

SolidCAM posts are GPPL procedures: a block starts with `@name`, ends with `endp`, and
`output` appends its arguments to the **current** NC line. A new NC line is started with
the `"\J"` argument. Without `"\J"` the whole header would land on one line and the
Server would reject the release (the hook must be on its own line). Print the Meimad
tokens as literal strings; do not bind them to job, part, or user fields.

### 7.1 Haas, FANUC, and Mazak posts (macro-B controls)

```text
; ---- Meimad canonical header: literal tokens, no CAM values ----
@start_program
    output "%"
    output "\J" "O" program_number
    output "\J" "(PART: [[MEIMAD:PART_NAME]])"
    output "\J" "(OPERATION: [[MEIMAD:OPERATION_NAME]])"
    output "\J" "(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])"
    output "\J" "(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])"
    output "\J" "(MACHINE: [[MEIMAD:MACHINE_ID]])"
    output "\J" "(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])"
    output "\J" "(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])"
    output "\J" "[[MEIMAD:VERIFICATION_HOOK]]"
; FANUC family only: open the print channel after the hook
    output "\J" "POPEN"
    output "\J" "[[MEIMAD:EVENT_CONTEXT]]"
    output "\J" "DPRNT[[[MEIMAD:PART_NAME]]]"
    output "\J" "[[MEIMAD:CYCLE_START]]"
    output "\J" "G90 G17 G40 G49 G80"
endp

; ---- tools, motion, cycles: the normal SolidCAM procedures, unchanged ----

@end_program
    output "\J" "[[MEIMAD:CYCLE_END]]"
; FANUC family only: close the print channel before M30
    output "\J" "PCLOS"
    output "\J" "M30"
    output "\J" "%"
endp
```

Omit the two `POPEN`/`PCLOS` lines in the Haas and Mazak posts. `CYCLE_START` is the last
line of `@start_program` and `CYCLE_END` the first line of `@end_program` because, for a
CAM-generated program, the whole program is one physical cycle; if the post emits an
in-program probing procedure after the last tool, `CYCLE_END` stays after it.

### 7.2 Okuma OSP post

```text
@start_program
    output "(PART: [[MEIMAD:PART_NAME]])"
    output "\J" "(OPERATION: [[MEIMAD:OPERATION_NAME]])"
    output "\J" "(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])"
    output "\J" "(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])"
    output "\J" "(MACHINE: [[MEIMAD:MACHINE_ID]])"
    output "\J" "(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])"
    output "\J" "(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])"
    output "\J" "[[MEIMAD:VERIFICATION_HOOK]]"
    output "\J" "[[MEIMAD:EVENT_CONTEXT]]"
    output "\J" "PUT '[[MEIMAD:PART_NAME]]'"
    output "\J" "WRITE C"
    output "\J" "[[MEIMAD:CYCLE_START]]"
    output "\J" "G15 H1"
endp

@end_program
    output "\J" "[[MEIMAD:CYCLE_END]]"
    output "\J" "M02"
endp
```

No `%`, no `O` name line, `M02` at the end, and the `$NAME.MIN%` transfer header is added
by the transfer tool, never by the post (section 6.4).

### 7.3 Notes for the SolidCAM writer

- Procedure names vary between SolidCAM versions and machine definitions
  (`@start_program`/`@end_program`, `@start_of_file`/`@end_of_file`); the required result
  is the literal NC of section 1, whichever procedures produce it.
- Keep `program_number`, tool, and coordinate formatting exactly as the existing post
  defines them; Meimad does not read them.
- Do not use `part_name`, job or user text, or any SolidCAM variable in place of a Meimad
  token, and do not add HTTP, database, environment, or file lookups to the post.
- Strings in GPPL are double-quoted; the tokens contain only `[`, `]`, `:`, `_`, and
  letters, none of which needs escaping.

## 8. Cimatron GPP examples

Cimatron GPP posts are `.exf` execution files made of named blocks. The tape-level blocks
are `BEGINNING OF TAPE:` (once, at the start of the NC file) and `END OF TAPE:` (once, at
the end); `OUTPUT \J "text" ;` writes a literal on a new line and every statement ends
with `;`. Emit the tokens before any executable modal or motion block.

### 8.1 Haas, FANUC, and Mazak posts (macro-B controls)

```text
BEGINNING OF TAPE:
        OUTPUT "%" ;
        OUTPUT \J "O1500" ;
        OUTPUT \J "(PART: [[MEIMAD:PART_NAME]])" ;
        OUTPUT \J "(OPERATION: [[MEIMAD:OPERATION_NAME]])" ;
        OUTPUT \J "(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])" ;
        OUTPUT \J "(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])" ;
        OUTPUT \J "(MACHINE: [[MEIMAD:MACHINE_ID]])" ;
        OUTPUT \J "(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])" ;
        OUTPUT \J "(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])" ;
        OUTPUT \J "[[MEIMAD:VERIFICATION_HOOK]]" ;
        OUTPUT \J "POPEN" ;                       { FANUC family only }
        OUTPUT \J "[[MEIMAD:EVENT_CONTEXT]]" ;
        OUTPUT \J "DPRNT[[[MEIMAD:PART_NAME]]]" ;
        OUTPUT \J "[[MEIMAD:CYCLE_START]]" ;
        OUTPUT \J "G90 G17 G40 G49 G80" ;

{ TOOL CHANGE:, LINEAR MOTION:, CIRCULAR MOTION:, cycles: the existing post blocks, unchanged }

END OF TAPE:
        OUTPUT \J "[[MEIMAD:CYCLE_END]]" ;
        OUTPUT \J "PCLOS" ;                       { FANUC family only }
        OUTPUT \J "M30" ;
        OUTPUT \J "%" ;
```

Omit the `POPEN`/`PCLOS` statements in the Haas and Mazak posts. The program number line
uses whatever the existing post already outputs (a literal, or the post's program-number
variable); Meimad does not read it.

### 8.2 Okuma OSP post

```text
BEGINNING OF TAPE:
        OUTPUT "(PART: [[MEIMAD:PART_NAME]])" ;
        OUTPUT \J "(OPERATION: [[MEIMAD:OPERATION_NAME]])" ;
        OUTPUT \J "(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])" ;
        OUTPUT \J "(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])" ;
        OUTPUT \J "(MACHINE: [[MEIMAD:MACHINE_ID]])" ;
        OUTPUT \J "(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])" ;
        OUTPUT \J "(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])" ;
        OUTPUT \J "[[MEIMAD:VERIFICATION_HOOK]]" ;
        OUTPUT \J "[[MEIMAD:EVENT_CONTEXT]]" ;
        OUTPUT \J "PUT '[[MEIMAD:PART_NAME]]'" ;
        OUTPUT \J "WRITE C" ;
        OUTPUT \J "[[MEIMAD:CYCLE_START]]" ;
        OUTPUT \J "G15 H1" ;

END OF TAPE:
        OUTPUT \J "[[MEIMAD:CYCLE_END]]" ;
        OUTPUT \J "M02" ;
```

### 8.3 Notes for the Cimatron writer

- Do not substitute Cimatron `PART_NAME`, the procedure name, the document name, or user
  text for the Meimad name placeholders. Those CAM values may remain in unrelated CAM
  comments, but they are never Meimad authority.
- Block names above are those of the classic GPP `.exf`; a GPP2 post keeps the same
  `OUTPUT \J "..." ;` statement, so check the sample `.exf` shipped with your Cimatron
  version for the exact block names it uses.
- Braces `{ }` are GPP comments in the listing above and are not written to the NC file.

## 9. Common mistakes and what the Server does with them

| Written by the post | Correct form | Result |
|---|---|---|
| `DPRNT[PART=[[MEIMAD:PART_NAME]]]` | `DPRNT[[[MEIMAD:PART_NAME]]]` | Release passes; the printed line is ignored and the Part is never identified |
| `POPEN` above `[[MEIMAD:VERIFICATION_HOOK]]` | hook first, then `POPEN` | Release rejected: `verification_placeholder_not_first` |
| `$O1500.MIN%` header line at the top (Okuma) | no transfer header in the released body | Release rejected: `verification_placeholder_not_first` |
| `[[MEIMAD:POSTPROCESSOR_ID]]` (from the draft) | remove it | Package creation fails: `production_package_placeholder_unknown` |
| `[[MEIMAD:EVENT_CONTEXT]]` twice | once | `production_package_placeholder_duplicate` |
| a `CYCLE_START`/`CYCLE_END` pair per tool | one pair around the whole cycle | `production_package_placeholder_duplicate` |
| `CYCLE_START` without `CYCLE_END` (or `CYCLE_END` in a subprogram file) | both in the main program | `production_package_cycle_marker_unpaired` |
| `CYCLE_END` above `CYCLE_START` | start first | `production_package_cycle_marker_order_invalid` |
| neither cycle marker | both, exactly once | `production_package_cycle_marker_required` |
| `G65 P9002 A1234. (MEIMAD VERIFY V1)` written by the post | `[[MEIMAD:VERIFICATION_HOOK]]` | Release rejected: active verification content |
| `(PART: 30P647004101-001)` typed in the post | `(PART: [[MEIMAD:PART_NAME]])` | Passes validation, but the header lies; the Server never cross-checks it |
| SolidCAM `output "..."` lines without `"\J"` | `output "\J" "..."` | Everything on one line; rejected as malformed or `verification_placeholder_not_first` |
| `DPRNT[[[MEIMAD:OPERATION_NAME]]]` | Operation name in a `( )` comment only | Prints an unreadable line; ignored |

## 10. Validation failures

Package/release validation rejects:

- a missing required key;
- malformed `[[MEIMAD:...]]` syntax;
- an unknown key;
- duplicate unique keys;
- a hook not on a standalone line or after executable code (including after `POPEN`,
  a `DPRNT`/`PUT`, or an Okuma `$NAME.MIN%` transfer header line);
- active Meimad verification logic embedded in the source;
- any unresolved required token in generated runnable NC;
- a template with neither cycle marker (`production_package_cycle_marker_required`),
  `CYCLE_START` without a matching `CYCLE_END` (or the reverse), or `CYCLE_END` before
  `CYCLE_START`;
- a legacy `(MEIMAD PACKAGE ... V1)` release on a Machine whose dialect is not `HAAS_NGC`
  (`production_package_dialect_legacy_unsupported`).

Validation does not check that the template's own statements match the Machine's dialect;
a Haas-style `DPRNT` in a template released to an `OKUMA_OSP` Machine passes validation and
alarms on the control. The pre-release checklist of section 13 catches the rest before the
first package build.

## 11. Compatibility and commissioning

The former exact `(MEIMAD PACKAGE VERIFY/CYCLE ... V1)` format remains a named
compatibility parser for immutable historical Haas releases. Do not emit it from a
new or edited postprocessor. Existing `.docx` and dated machine-test documents
are historical evidence, not current postprocessor instructions.

Generated verification and Offset Loader code still requires the existing
Machine-specific review and bounded no-motion commissioning on every control. This
document does not declare any protected macro, subprogram, or controller interlock
commissioned.

## 12. Troubleshooting failures seen in practice

- **`production_package_placeholder_unknown: Unknown Meimad placeholder '<KEY>' on line
  N.`** — the source template contains a `[[MEIMAD:<KEY>]]` token that is not in the
  key set of section 1 (most commonly `POSTPROCESSOR_ID`, which is not and has never been
  a valid key; see the draft warning above). Fix the postprocessor to stop emitting it,
  then release a corrected NC.
- **`production_package_placeholder_duplicate: Canonical NC template must contain
  exactly one [[MEIMAD:EVENT_CONTEXT]].`** — the template emits `EVENT_CONTEXT` twice, or
  a cycle marker more than once. The implemented protocol requires exactly one occurrence
  of each; one `CYCLE_START`/`CYCLE_END` pair around the whole cycle.
- **The Part never appears in monitoring although DPRNT is connected.** — the printed
  line is not part-number-shaped: a `PART=` prefix, a lowercase or underscore character, a
  Part number without a `-`/`.` separator, or a `.CNC` suffix. Print the bare Part
  number (section 2) and check the raw diagnostics (`GET /api/v1/machines/{id}/cnc-diagnostics`)
  for what the control actually sent.
- **Parts are produced but the Run count stays at zero.** — in this order: the package was
  built before 2026-09-18 from a template without the `CYCLE_START`/`CYCLE_END` pair
  (rebuilding it now fails with `production_package_cycle_marker_required`; re-release the
  NC with the pair), the Machine's Server Verification is disabled (the markers are
  removed), or the `CST`/`CEN` lines never reach the Server
  (FANUC: `PCLOS` before the `CEN` block; Mazak: the print file is not shared or not
  polled; Okuma: `PUT` without `WRITE C`).
- **The control alarms on `G103`, `#10504`, `DPRNT`, or `G65` in a generated package.** —
  the Machine's NC dialect in Setup is wrong for that control (a FANUC or Mazak with
  `HAAS_NGC`, or an Okuma with any macro-B dialect). Set the dialect, re-enter the
  verification variables for that control, rebuild the package.
- **`ncDialect: verification_enabled` when saving a Machine.** — disable Server
  Verification for that Machine before changing its dialect, then re-enter the variable
  mappings in the new control's range.
- **FANUC: the first DPRNT line after program start is missing or garbled.** — an older
  Server did not strip the DC2 code that `POPEN` sends; upgrade the Server. If lines are
  still missing, the bridge is not in TCP-server mode or its serial settings do not match
  parameters 101–103.
- **`verification_placeholder_not_first`** on a release that looks correct — a
  non-comment line precedes the hook: `POPEN`, a `DPRNT`/`PUT`, a `G`/`M`/`T`/`S` word, or an
  Okuma `$NAME.MIN%` header. Move the hook above it or remove the line. In a SolidCAM post
  this is usually a missing `"\J"`, which glued the hook to the previous line.

If a release keeps failing after a fix, use "View NC File (read-only)" or download the
release directly and search for `[[MEIMAD:` to see every token and line number
actually present, rather than assuming the macro emits what its source code says; a
stale CAM export or wrong output folder can silently ship an older file.

## 13. Pre-release checklist for the writer

Run the post once on a test job and check the NC file, not the post source:

1. Search for `[[MEIMAD:`; there are 12 tokens: `PART_NAME` twice (header comment and the
   print line), every other key exactly once.
2. The first non-comment, non-`%`, non-`O` line is `[[MEIMAD:VERIFICATION_HOOK]]`.
3. `[[MEIMAD:EVENT_CONTEXT]]` is on its own line after the hook (after `POPEN` on FANUC).
4. The print line is exactly `DPRNT[[[MEIMAD:PART_NAME]]]`, or `PUT '[[MEIMAD:PART_NAME]]'`
   followed by `WRITE C` on Okuma, with nothing else on the line.
5. `[[MEIMAD:CYCLE_START]]` precedes the first `G`/`T`/`S` word and `[[MEIMAD:CYCLE_END]]`
   follows the last cut on the successful path, before `M30`/`M02`; each once.
6. No real Part number, Operation name, Machine number, or verification call appears
   anywhere the post wrote it.
7. FANUC: `POPEN` after the hook, `PCLOS` after `CYCLE_END`. Okuma: no `%`, no `O` name
   line, no `$NAME.MIN%` header, `M02` at the end.
8. Release it to the Machine with the right NC dialect and build one package; open the
   generated runnable NC and compare it with section 5.

## 14. References

- [PostProcessor -> Production Package Creator contract](postprocessor-production-package-contract.md)
- [Postprocessor writer documentation audit, 2026-09-18](postprocessor-writer-documentation-audit-2026-09-18.md)
- [Production Package implementation task](../TASKS_FOR_CODEX_PRODUCTION_PACKAGE.md)
- [Production Run architecture](production-run-architecture.md)
- [Haas verification response algorithm](haas-verification-response-algorithm.md)
- [CNC commissioning checklist](cnc-commissioning-checklist.md)
- [User help: CNC connection, DPRNT source, FANUC FOCAS](user-help.md)
