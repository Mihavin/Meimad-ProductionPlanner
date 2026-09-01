# Meimad Haas NGC postprocessor guide

This is the complete postprocessor-facing specification for Meimad Production
Planner. It is written for SolidCAM and Cimatron post writers who do not need to
know the Server database, tablet, or verification mathematics.

## The three lines a post may write

The released NC is a **source template**, not the final Machine file. Write the
following verification placeholder exactly once, before the first executable
block:

```gcode
(MEIMAD PACKAGE VERIFY V1 NCID=817426)
```

`817426` is an example. Supply a new Server-approved six-digit NC identity from
`100000` through `999999` for every changed release.

If Meimad cycle counting is enabled for this post, also write exactly one pair:

```gcode
(MEIMAD PACKAGE CYCLE START V1)
...
(MEIMAD PACKAGE CYCLE END V1)
```

The START marker belongs immediately before the physical machining cycle. The
END marker belongs only on the common successful path after that whole physical
cycle has completed. The markers are comments in the released template. The
Server resolves them when it creates a Machine-specific Production Package.

## What the post must not write

The normal CAM post must not write:

- `G65 P9001` or an Offset Loader;
- `G65 P9002` or another active Server-verification call;
- `(MEIMAD VERIFY V1)`;
- `OLC`, `SVS`, or `SVF` DPRNT records;
- raw `CST` or `CEN` DPRNT records;
- a Machine-specific macro number, protected-variable number, nonce, response,
  Offset Loader token, Machine credential, or tablet value.

The Server owns those values. A verification-enabled CNC package receives the
currently configured verification call, commissioned cycle-event blocks, and a
unique package-specific Offset Loader. A verification-disabled CNC package has
all three package markers removed and receives no active verification code. A
Manual Machine package receives no CNC executable.

## Exact marker grammar

The verification line is a full-line Haas comment with this exact structure:

```text
(MEIMAD PACKAGE VERIFY V1 NCID=dddddd)
```

Rules:

1. `dddddd` is exactly six decimal digits from `100000` through `999999`.
2. There is exactly one ASCII space between words.
3. There are no spaces around `=`.
4. There is no decimal point after the identity.
5. The marker occurs before the first executable block. `%`, an `O` header,
   blank lines, and full-line comments may precede it.
6. Do not put an `N` sequence word on a marker line.
7. Do not split a marker across output lines.
8. Each separately released main NC file has its own identity and marker.

Cycle markers are optional as a pair, but never individually:

```text
(MEIMAD PACKAGE CYCLE START V1)
(MEIMAD PACKAGE CYCLE END V1)
```

START must precede END. There may be ordinary G-code between them. Neither may
occur more than once in one released file.

## Correct and incorrect NC templates

Correct minimum template:

```gcode
%
O01995 (PART-100 OP10)
(PART: PART-100)
(CASE: CASE-100)
(OPERATION: OP10)
(REVISION: A)
(MEIMAD PACKAGE VERIFY V1 NCID=817426)
G17 G40 G49 G80 G90
M30
%
```

Correct template with one physical cycle:

```gcode
%
O01995 (PART-100 OP10)
(PART: PART-100)
(OPERATION: OP10)
(MEIMAD PACKAGE VERIFY V1 NCID=817426)
G17 G40 G49 G80 G90
T1 M06
G54
(MEIMAD PACKAGE CYCLE START V1)
M03 S2500
G00 X0. Y0.
G01 Z-2. F100.
G00 Z50.
M05
(MEIMAD PACKAGE CYCLE END V1)
M30
%
```

Wrong because executable code precedes the identity marker:

```gcode
G90
(MEIMAD PACKAGE VERIFY V1 NCID=817426)
```

Wrong because this is an active Machine-specific call in a source template:

```gcode
G65 P9002 A817426. (MEIMAD VERIFY V1)
```

Wrong because only one cycle marker is present:

```gcode
(MEIMAD PACKAGE CYCLE START V1)
M30
```

## Required post inputs

| Input | Requirement |
|---|---|
| Meimad template enabled | Required for a Meimad production release. |
| NC identity | Exactly six digits; new for each exact release. |
| Cycle markers enabled | Default `No`; enable only when the physical cycle boundary is known. |
| Part / Case / Operation / Revision | Human-readable header comments when available. |

The post does **not** need a verify-program number, challenge-program number,
Offset Loader token, macro version, sequence variable, Machine IP, or Machine
name. Those are resolved by the Server from the assigned Machine configuration.

Stop posting with a clear error when Meimad mode is enabled and the NC identity
is missing or outside the valid range. Do not derive the identity from the
O-number, date, part, file name, or operation; those values can repeat.

## Generic implementation logic

```text
begin main output file
    write percent and O-number
    write full-line identification comments

    if Meimad mode is not enabled
        stop: this output is not a Meimad release template
    end if

    if NC identity is not an integer from 100000 through 999999
        stop with a clear error
    end if

    write "(MEIMAD PACKAGE VERIFY V1 NCID=" + NC identity + ")"

    write normal Haas initialization and machining

    if cycle markers are enabled
        write START once before the physical cycle
        write END once on the common successful completion path
    end if
end main output file
```

Call the marker-writing routine once per **output file**, not once per CAM job,
procedure, operation, tool, or subprogram.

## SolidCAM GPPL example

Names differ between customer posts. Keep the existing file-open and validation
procedures and add one shared marker routine after header comments but before
the normal start codes.

```text
@start_of_file
    call @usr_sof_character
    call @usr_sof_progname
    call @usr_sof_commentsbeforecodes

    call @usr_meimad_package_marker

    call @usr_sof_gmcodes
    call @usr_sof_commentsaftercodes
endp

@usr_meimad_package_marker
    if iVMID_MEIMAD_ENABLED ne 1
        {message,'Meimad template mode must be enabled'}
        exit
    endif

    if iVMID_MEIMAD_NC_ID lt 100000 or iVMID_MEIMAD_NC_ID gt 999999
        {message,'Meimad NC ID must contain six digits'}
        exit
    endif

    {nl,'(MEIMAD PACKAGE VERIFY V1 NCID='iVMID_MEIMAD_NC_ID')'}
endp
```

The exact error/abort statement is post-family dependent; use the customer
post's existing fatal-validation pattern. Ensure numeric formatting adds no
sign, grouping separator, spaces, or decimals.

If one SolidCAM project generates three separately released main files, call
the routine once in each file-open path and require three different identities.
Do not emit the marker from `@start_of_job`, tool-change, or technology-cycle
procedures that can run more than once.

For optional cycle markers, call these only at the approved whole-part boundary:

```text
{nl,'(MEIMAD PACKAGE CYCLE START V1)'}
    ; existing physical cycle output
{nl,'(MEIMAD PACKAGE CYCLE END V1)'}
```

## Cimatron original GPP example

Declare one sequencing input and validate it using the customer's existing GPP
fatal-error convention. Write the marker inside `BEGINNING OF TAPE`, after
header comments and before any Haas command.

```text
FORMAT (SEQUENCING) MEIMAD_NC_ID ;

INTERACTION (SEQUENCING)
    "MEIMAD NC ID - SIX DIGITS"
    MEIMAD_NC_ID = 0 ;

BEGINNING OF TAPE:
    * Use the installed post's fatal-stop convention here when invalid.
    OUTPUT "% " \J "O" PGN ;
    * Existing full-line PART/CASE/OPERATION/REVISION comments.

    OUTPUT $ " (MEIMAD PACKAGE VERIFY V1 NCID="
        MEIMAD_NC_ID ")" ;

    * The existing first executable Haas block follows.
    OUTPUT $ " G17 G40 G49 G80 G90" ;
```

Do not put the marker in `BEGINNING OF PROC`, beginning-of-toolpath, milling,
drilling, or tool-change blocks. Those blocks may run many times.

## Cimatron GPP2 example

```text
FORMAT (SEQUENCING) MEIMAD_NC_ID ;

INTERACTION (SEQUENCING)
    "Meimad NC ID - six digits"
    MEIMAD_NC_ID = 0 ;

BEGINNING OF TAPE:
    IF (MEIMAD_NC_ID < 100000 || MEIMAD_NC_ID > 999999)
        GPP_STOP "Meimad NC ID must be 100000 through 999999" ;
    END_IF;

    OUTPUT "% " \J "O" PGN ;
    // Existing full-line header comments.
    OUTPUT $ " (MEIMAD PACKAGE VERIFY V1 NCID="
        MEIMAD_NC_ID ")" ;
    OUTPUT $ " G17 G40 G49 G80 G90" ;
```

If split output can create several main files, the post must request/receive one
new identity per file. If the current interaction supplies only one identity,
stop the release or disable split output; never reuse it silently.

### Cimatron cycle-marker example

Only after the shop has identified one true physical cycle boundary:

```text
OUTPUT $ " (MEIMAD PACKAGE CYCLE START V1)" ;
* Existing blocks that produce one complete physical cycle.
OUTPUT $ " (MEIMAD PACKAGE CYCLE END V1)" ;
```

Do not use one pair per Cimatron procedure unless one procedure is proven to
equal one physical completed part cycle. For a multi-output coupled NC cycle,
write one pair around the complete atomic cycle, not one pair per output part.

## What the Server produces

The post writer does not generate these files, but understanding the boundary
prevents duplicated code:

| Assigned Machine | Runnable package NC | Offset Loader |
|---|---|---|
| CNC, verification enabled | Verification marker becomes the configured active hook; cycle pair becomes commissioned V10 CST/CEN blocks. | Server creates a fresh package-specific loader. |
| CNC, verification disabled | Verification and cycle markers are removed; no active Meimad verification remains. | None. |
| Manual | No CNC runnable NC is required. | None. |

The immutable source-template hash and every generated artifact hash are stored
separately. Package generation never edits the approved source release.

## Release validation checklist

- [ ] Exactly one verification placeholder is present.
- [ ] It is before the first executable block.
- [ ] Its identity contains exactly six digits and has no decimal point.
- [ ] The identity is new for this exact release.
- [ ] There is no active `(MEIMAD VERIFY V1)` or `G65 P9001/P9002` content.
- [ ] Cycle placeholders are either both absent or exactly one ordered pair.
- [ ] START/END surround one complete successful physical cycle.
- [ ] The post does not write OLC/SVS/SVF/CST/CEN DPRNT records.
- [ ] A split-output job uses a distinct identity in each released main file.
- [ ] The final posted bytes—not an earlier preview—pass Server publication.

## Troubleshooting publication errors

| Symptom | Correction |
|---|---|
| Verification marker missing | Call the shared marker routine once in the main-file opening path. |
| Marker is late | Move it before all safety codes, tool calls, macro calls, and motion. |
| Marker appears twice | Remove calls from job/tool/procedure paths; keep only the file-level call. |
| Identity rejected | Supply a new integer from `100000` through `999999`. |
| Active verification rejected | Remove `G65`/`(MEIMAD VERIFY V1)` from the source post. |
| Cycle structure rejected | Emit no cycle markers, or one START followed by one END. |
| Disabled package still contains marker | This is a Server package-build failure, not something to repair in the post output. |

## References

- [Functional specification](functional-spec.md)
- [API contract](api-contract.md)
- [Production Run architecture](production-run-architecture.md)
- [Haas verification response architecture](haas-verification-response-algorithm.md)
