# Simple Meimad guide for Haas postprocessor programmers

## What you need to change

This guide explains the small changes needed in a Haas NGC postprocessor so its G-code works with Meimad.

You do not need to understand the Meimad Server, database, tablet, network security, or verification mathematics. You only need to generate the correct G-code lines in the correct places.

If you remember only five rules, remember these:

1. Write one Meimad verification call near the top of every released NC file.
2. It must be the first executable G-code line.
3. Give every new NC release a new six-digit NC ID.
4. Do not write OLC, SVS, or SVF DPRNT lines in the normal NC post. The protected macros write them.
5. Keep CST/CEN cycle DPRNT output disabled until Meimad approves the exact Machine setup.

## 1. The most important line

The post must write this line once:

```gcode
G65 P9002 A817426. (MEIMAD VERIFY V1)
```

Meaning:

| Text | Simple meaning |
|---|---|
| `G65` | Call a macro program. |
| `P9002` | Call protected program O9002. The approved Machine setting may use another O9xxx number. |
| `A817426.` | The six-digit Meimad NC ID. The final decimal point is required in post output. |
| `(MEIMAD VERIFY V1)` | Fixed marker used by Meimad. Write it exactly like this. |

Do not copy `817426`. It is only an example. Every newly released NC file needs a new ID between `100000` and `999999`.

## 2. Where the line goes

The verification call must be the first executable line.

Comments, `%`, and the O-number may appear before it. Normal safety codes must appear after it.

Correct:

```gcode
%
O01994 (PART-100 OP10)
(PART: PART-100)
(OPERATION: 10)
G65 P9002 A817426. (MEIMAD VERIFY V1)
G17 G40 G49 G80 G90
```

Wrong—`G90` executes before verification:

```gcode
%
O01994
G90
G65 P9002 A817426. (MEIMAD VERIFY V1)
```

Also wrong—two verification calls:

```gcode
G65 P9002 A817426. (MEIMAD VERIFY V1)
G65 P9002 A817427. (MEIMAD VERIFY V1)
```

The complete NC file must contain exactly one `(MEIMAD VERIFY V1)` marker.

## 3. Values the post needs

Add these properties or inputs to the post:

| Post input | Required value |
|---|---|
| Meimad enabled | Yes/No. It must be Yes for a Meimad production release. |
| NC ID | A new number from 100000 to 999999. |
| Verify program | Normally 9002. Use the number approved for the target Machine. |
| Part | Part number or stable part name. |
| Case | Meimad Case name or number, when available. |
| Operation | Operation number/name, when available. |
| Revision | NC or part revision, when available. |
| Cycle DPRNT enabled | Default No. Enable only after Meimad commissioning. |
| Macro version | Needed only when cycle DPRNT is enabled. |

Stop post generation and show an error if Meimad is enabled but the NC ID or verification program is missing.

Do not automatically build the NC ID from the O-number, date, part number, or file name. These values can repeat. The NC ID must be new for each exact released file.

## 4. Header comments

Write simple identification comments before the verification call:

```gcode
(PART: 456-123-A)
(CASE: CASE-456)
(OPERATION: 20)
(REVISION: B)
```

Rules:

- Write one clear `PART:` value.
- Do not write two different part names in the header.
- The O-number is not the Meimad NC ID.
- The comments are useful to people, but the six-digit ID in the verification call identifies the exact NC release.

## 5. Simple post logic

The beginning of the post should work in this order:

```text
write %
write O-number
write PART / CASE / OPERATION / REVISION comments
check that the Meimad NC ID is six digits
write the O9002 verification call
write normal Haas safety codes
write the rest of the normal NC program
```

Platform-independent pseudocode:

```text
function writeMeimadVerification() {
    if meimadEnabled is false:
        return

    if ncId is not between 100000 and 999999:
        stop post with "Meimad NC ID must be six digits"

    if verifyProgram is not an approved O9xxx program:
        stop post with "Invalid Meimad verify program"

    write "G65 P" + verifyProgram
        + " A" + ncId + "."
        + " (MEIMAD VERIFY V1)"
}
```

Call this function one time from the main program opening function. Do not call it from tool-change code, operation code, subprogram code, or the program footer.

## 6. Special instructions for SolidCAM post writers

SolidCAM posts use the GPPL language for G-code output. The `.vmid` file describes the Machine and controller/output settings, while the `.gpp` file contains the post logic. Add the Meimad output routine to the GPPL output logic. Do not put executable G-code into the Machine kinematics definition.

### Where to add the hook

Find the one GPPL procedure in your existing post that writes the beginning of the main NC output file. It normally writes `%`, the O-number, program comments, and then the Haas safety blocks.

Change that procedure to use this order:

```text
write % and O-number
write PART / CASE / OPERATION / REVISION comments
call one shared GPPL routine that writes the Meimad O9002 hook
write the normal Haas safety blocks
```

Do not add the hook to a SolidCAM job-start, tool-start, operation-start, or subprogram procedure that can run more than once. One generated NC file must contain exactly one hook.

Use the existing GPPL text/block output command from the post. The finished output line—not the GPPL source syntax—must be exactly this shape:

```gcode
G65 P9002 A817426. (MEIMAD VERIFY V1)
```

If the post automatically adds `N` sequence numbers, an `N` number may appear before `G65`. Do not let the formatter add another address, remove the decimal point, change the fixed marker, or split the call across lines.

### SolidCAM properties

Provide the NC ID and Meimad enable switch as post inputs. A SolidCAM implementation may expose user-defined settings through the VMID/VMC and read them from GPPL, or may receive them from the controlled release workflow. Whichever method is used:

- show the NC ID clearly to the CAM user;
- validate that it contains exactly six digits;
- never keep a sample NC ID as the production default;
- default cycle DPRNT to disabled;
- stop posting when required values are missing.

Keep the Meimad-enabled Haas post and its matching `.gpp`/`.vmid` files together. Test the pair actually selected by the CAM user; editing another post with a similar display name does not change the selected output.

### SolidCAM multi-job and subprogram warning

A SolidCAM project can contain many CAM jobs while still producing one main NC file. The verification hook belongs to the file, not to each job. If the post creates separate main files, each released file needs its own new NC ID and one hook. Do not reuse one NC ID automatically across multiple generated files.

Put optional CST immediately before the post's first counted machining output for one physical cycle. Put CEN only in the final-success branch for that cycle. Do not put CEN in a general `end_of_job`-style procedure if that procedure can also run after an incomplete or aborted path.

### SolidCAM delivery check

Deliver the edited `.gpp`, its matching `.vmid`, and generated test files. Regenerate G-code from SolidCAM and inspect the final file; do not accept a manually edited NC sample as proof that the GPPL change works.

### SolidCAM GPPL example

This example shows the normal integration shape used by many SolidCAM GPPL posts. Procedure names inside a real post may differ. Keep the existing file-opening helpers and insert the Meimad call between the header-comment helper and the first G/M-code helper.

```text
; SOLIDCAM GPPL INTEGRATION EXAMPLE
; Assumed VMID variables:
;   iVMID_MEIMAD_ENABLED
;   iVMID_MEIMAD_NC_ID
;   iVMID_MEIMAD_VERIFY_PROGRAM

@start_of_file

    call @usr_sof_character
    call @usr_sof_progname
    call @usr_sof_commentsbeforecodes

    ; Add this one call here.
    call @usr_meimad_verification

    ; Existing Haas executable output must remain after Meimad.
    call @usr_sof_gmcodes
    call @usr_sof_commentsaftercodes

endp


@usr_meimad_verification

    if iVMID_MEIMAD_ENABLED eq 1

        ; Validate the three VMID values with the error mechanism
        ; already used by this post before reaching this output.

        {nl,'G65 P'iVMID_MEIMAD_VERIFY_PROGRAM
            ' A'iVMID_MEIMAD_NC_ID
            '. (MEIMAD VERIFY V1)'}

    endif

endp
```

The line break inside the GPPL source is only for document readability. If the local GPPL version does not allow the code-generation expression to span source lines, place the expression on one line:

```text
{nl,'G65 P'iVMID_MEIMAD_VERIFY_PROGRAM' A'iVMID_MEIMAD_NC_ID'. (MEIMAD VERIFY V1)'}
```

The generated G-code must be one line:

```gcode
G65 P9002 A817426. (MEIMAD VERIFY V1)
```

If the existing post writes `G17 G21 G80 G90` inside `@usr_sof_gmcodes`, the placement above is correct. Do not put `call @usr_meimad_verification` after that helper.

Optional cycle routines can use the same GPPL output style, but the variables below are placeholders. Map them only after Meimad approves the persistent sequence source.

```text
@usr_meimad_cycle_start

    if iVMID_MEIMAD_CYCLE_DPRNT eq 1
        {nb,'DPRNT[MEIMAD/V/1/EVENT/CST/ID/'sMEIMAD_START_ID
            '/SEQ/'iMEIMAD_START_SEQ
            '/MACROVERSION/'iVMID_MEIMAD_MACRO_VERSION
            '/PROGRAM/'iVMID_MEIMAD_NC_ID']'}
    endif

endp


@usr_meimad_cycle_end

    if iVMID_MEIMAD_CYCLE_DPRNT eq 1
        {nb,'DPRNT[MEIMAD/V/1/EVENT/CEN/ID/'sMEIMAD_END_ID
            '/SEQ/'iMEIMAD_END_SEQ
            '/MACROVERSION/'iVMID_MEIMAD_MACRO_VERSION
            '/PROGRAM/'iVMID_MEIMAD_NC_ID']'}
    endif

endp
```

Call `@usr_meimad_cycle_start` once at the approved physical cycle-start boundary. Call `@usr_meimad_cycle_end` only from the complete-success branch. Do not place either call in `@start_of_job` or `@end_of_job` merely because the names look similar to cycle start/end.

## 7. Special instructions for Cimatron post writers

Cimatron supports the original GPP system and GPP2. First identify which system the selected Haas post actually uses. Do not edit a GPP post when production selects GPP2, or the reverse.

- Original GPP post logic is developed in the GPP program source and compiled for use by Cimatron.
- GPP2 uses its post program and configuration files, including the EX2/DF2 environment.
- In both systems, the requirement is the same: the final G-code file must contain one hook as its first executable block.

### Where to add the hook

Find the one top-level GPP or GPP2 block that writes the beginning of each main output file. Add one shared Meimad output procedure and call it after the O-number/header comments but before the first safety or motion block.

Do not call it from procedure-start, tool-change, drilling, milling, or other toolpath handlers. Those handlers may run many times.

Use the post's normal output statement to produce this final line:

```gcode
G65 P9002 A817426. (MEIMAD VERIFY V1)
```

The exact GPP/GPP2 statement syntax depends on the existing post and Cimatron version. Copy the output style already used by that post for literal Haas blocks, but verify the final G-code text against this guide.

### Cimatron post parameters

Expose the Meimad NC ID, enable switch, verify-program number, and cycle-DPRNT switch as G-code/post parameters when practical. For GPP2, configuration-associated variables can be presented through the post interaction and read by the post program. Regardless of the UI method, the post must validate the value before writing the hook.

### Important Cimatron split-output warning

Cimatron can split posted output by group, toolpath folder, tool change, UCS, or procedure. Every resulting main NC file is a different release candidate and needs:

- exactly one verification hook;
- its own new six-digit NC ID;
- the hook as that file's first executable block.

If the post receives only one NC ID but Cimatron is configured to create several files, stop posting with a clear error or disable split output for that release. Never place the same NC ID into every split file.

Also check any “script after postprocessor” step. It must not insert a second hook, move the hook, or change the final released bytes after review.

### Cimatron cycle events

If CST/CEN is later commissioned, add the calls to the top-level successful machining flow—not to every Cimatron procedure automatically. Cimatron procedure count does not necessarily equal physical part-cycle count. The post writer must use the Meimad-approved physical cycle boundary.

Place CEN only where the post knows the full physical cycle completed. Toolpath completion, one procedure completion, or reaching a common file footer is not sufficient by itself.

### Cimatron delivery check

For original GPP, deliver the editable source, compiled post used for the test, and generated G-code. For GPP2, deliver the matching program/configuration files and generated G-code. Run the post from Cimatron's actual Post Process dialog, inspect every output file when splitting is enabled, and review the Cimatron output log for warnings.

### Cimatron original GPP example

The following example follows the original GPP file structure. Add the declarations near the other `FORMAT` and `INTERACTION` statements. Add the output line inside the existing `BEGINNING OF TAPE` block.

```text
* CIMATRON ORIGINAL GPP INTEGRATION EXAMPLE

FORMAT (SEQUENCING) MEIMAD_NC_ID ;

INTERACTION (SEQUENCING)
    "MEIMAD NC ID - SIX DIGITS"
    MEIMAD_NC_ID = 0 ;

NEW_LINE_IS $ ;

* Keep the post's existing NEW_LINE_IS statements here.

BEGINNING OF TAPE:

    * Keep the existing initialization.
    OUTPUT "% " \J "O" PGN ;

    * Keep existing PART/CASE/OPERATION comment output here.
    * No executable G-code may be output yet.

    OUTPUT $ " G65 P9002 A" MEIMAD_NC_ID
        ". (MEIMAD VERIFY V1)" ;

    * The existing first Haas safety line now follows the hook.
    OUTPUT $ " G17 G40 G49 G80 G90" ;
```

Important: the `SEQUENCING` format controls how the number is printed. Confirm that `MEIMAD_NC_ID` produces exactly six digits with no sign, grouping separator, or decimal digits. The literal period after the variable creates the required Haas `A817426.` form. Add the range check using the same stop/error pattern already used by the customer's original GPP post.

### Cimatron GPP2 example with validation

GPP2 supports `BOOLEAN_` interaction values and `GPP_STOP`, so the post can reject missing inputs before producing G-code.

```text
// CIMATRON GPP2 INTEGRATION EXAMPLE

FORMAT (SEQUENCING) MEIMAD_NC_ID MEIMAD_VERIFY_PROGRAM ;

INTERACTION (BOOLEAN_)
    "Enable Meimad verification"
    MEIMAD_ENABLED = FALSE_ ;

INTERACTION (SEQUENCING)
    "Meimad NC ID - six digits"
    MEIMAD_NC_ID = 0 ;

INTERACTION (SEQUENCING)
    "Meimad verify O-program"
    MEIMAD_VERIFY_PROGRAM = 9002 ;

BEGINNING OF TAPE:

    IF (MEIMAD_ENABLED == TRUE_)

        IF (MEIMAD_NC_ID < 100000 || MEIMAD_NC_ID > 999999)
            GPP_STOP "Meimad NC ID must be 100000 through 999999" ;
        END_IF;

        IF (MEIMAD_VERIFY_PROGRAM < 9000 ||
            MEIMAD_VERIFY_PROGRAM > 9999)
            GPP_STOP "Meimad verify program must be O9000-O9999" ;
        END_IF;

    END_IF;

    OUTPUT "% " \J "O" PGN ;

    // Keep existing full-line header comments here.
    // Do not output any G/M block before the next statement.

    IF (MEIMAD_ENABLED == TRUE_)
        OUTPUT $ " G65 P" MEIMAD_VERIFY_PROGRAM
            " A" MEIMAD_NC_ID
            ". (MEIMAD VERIFY V1)" ;
    END_IF;

    // Existing executable initialization follows.
    OUTPUT $ " G17 G40 G49 G80 G90" ;
```

If Meimad files are mandatory for this post, remove the enable switch and always validate/output the hook. A production post must never silently generate an unverified release because a remembered interaction value was set to false.

### Cimatron optional cycle-output example

The same `OUTPUT` statement can generate CST and CEN after commissioning:

```text
// Call from the approved physical cycle-start block.
OUTPUT $ " DPRNT[MEIMAD/V/1/EVENT/CST/ID/" MEIMAD_START_ID
    "/SEQ/" MEIMAD_START_SEQ
    "/MACROVERSION/" MEIMAD_MACRO_VERSION
    "/PROGRAM/" MEIMAD_NC_ID "]" ;

// Call only from the complete-success block.
OUTPUT $ " DPRNT[MEIMAD/V/1/EVENT/CEN/ID/" MEIMAD_END_ID
    "/SEQ/" MEIMAD_END_SEQ
    "/MACROVERSION/" MEIMAD_MACRO_VERSION
    "/PROGRAM/" MEIMAD_NC_ID "]" ;
```

`MEIMAD_START_ID`, `MEIMAD_END_ID`, and the Meimad sequence variables are intentionally not declared in this example. Macro v6 proposes one configured persistent Machine-specific counter shared by all event emitters, but it is not physically commissioned. Do not reuse Cimatron's ordinary G-code `N` block sequence, Haas parts counter, or `#3001` as the Meimad event sequence.

## 8. Complete normal NC example

This is a complete no-motion example. It is useful for checking the post output without loading offsets or moving the Machine.

```gcode
%
O01995 (MEIMAD POST TEST)
(PART: TESTSERVERVERIFICATION005)
(CASE: TESTSERVERVERIFICATION005)
(OPERATION: 10 TEST1)
(REVISION: TEST)
G65 P9002 A817426. (MEIMAD VERIFY V1)
(NO MOTION)
(NO OFFSET LOADS)
(NO CYCLE DPRNT)
G17 G40 G49 G80 G90
M30
%
```

Before creating a real release, replace `817426` with a new six-digit NC ID. Do not use the sample number.

## 9. The Offset Loader call

The Offset Loader is a separate NC file. It is not the normal machining program.

If your work includes the Offset Loader generator, write this call only after all offsets were written and checked successfully:

```gcode
G65 P9001 A483920. B817426.
M30
```

Meaning:

| Text | Simple meaning |
|---|---|
| `P9001` | Call the protected Offset Loader challenge macro O9001. |
| `A483920.` | Six-digit Offset Loader token supplied by the Meimad Server. |
| `B817426.` | The same NC ID used by the main machining program. |
| `M30` | Normal end after the successful call. |

Rules:

- Both numbers must have a decimal point.
- Do not invent the Offset Loader token.
- Do not put this call in the normal machining post.
- Do not reach this call after an offset write/readback failure.
- Put it near the end of the successful Offset Loader path, immediately before `M30`.

Example shape:

```gcode
%
O01993 (MEIMAD OFFSET LOADER)
(OFFSET WRITE COMMANDS)
(OFFSET READBACK CHECKS)
(IF ANY CHECK FAILS, ALARM AND DO NOT CONTINUE)
G65 P9001 A<OFFSET_TOKEN>. B<NC_ID>.
M30
%
```

The final generated file must contain real six-digit numbers, not angle-bracket placeholders.

## 10. Who writes each DPRNT event

`DPRNT` sends one text line from the Haas control to the Meimad CNC communication service.

The normal post does not write every Meimad event.

| Event | Meaning | Who writes it? |
|---|---|---|
| `OLC` | Offset Loader completed | Protected O9001 macro. Not the normal post. |
| `SVS` | Verification succeeded | Protected O9002 input macro and O9003 finalizer in the v6 candidate. Not the normal post. |
| `SVF` | Verification failed | Protected O9002 input macro and O9003 finalizer in the v6 candidate. Not the normal post. |
| `CST` | Production cycle started | Normal post, only after commissioning. |
| `CEN` | Production cycle completed | Normal post, only after commissioning. |

The normal post must not print fake success or failure messages. Never add these lines to the normal post:

```text
EVENT/OLC
EVENT/SVS
EVENT/SVF
```

There are other Meimad workflow events, but they are not postprocessor output. The post writer does not need to generate them.

## 11. Optional CST and CEN cycle events

Most post writers should initially leave this feature off.

Enable CST/CEN only when Meimad gives you:

- the approved macro/interface version;
- the approved sequence-number source;
- the exact cycle-start position;
- the exact successful cycle-end position;
- the approved QC hold/release behavior.

If any value is missing, generate no CST and no CEN.

### Cycle start

CST means “this physical machining cycle has started.” Put it immediately before the first counted machining action, after verification and any required QC approval.

Example format:

```gcode
DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-817426-S-201/SEQ/201/MACROVERSION/6/PROGRAM/817426]
```

### Cycle end

CEN means “this physical machining cycle completed successfully.” Put it only on the normal successful path, after all required machining and checks.

Example format:

```gcode
DPRNT[MEIMAD/V/1/EVENT/CEN/ID/NC-817426-E-202/SEQ/202/MACROVERSION/5/PROGRAM/817426]
```

In this example:

- the NC ID is `817426`;
- the CST sequence is `201`;
- the CEN sequence is the next number, `202`;
- the macro/interface version is `5`;
- the two event IDs are unique.

Do not hard-code these sample values.

### Simple placement example

This is only a placement example. It is not ready to run because the sequence-generation method is not shown.

```gcode
G65 P9002 A817426. (MEIMAD VERIFY V1)
G17 G40 G49 G80 G90

(NORMAL SETUP, TOOLS, OFFSETS, AND PROBING)
(APPROVED QC HOLD/RELEASE POINT)

DPRNT[MEIMAD/V/1/EVENT/CST/ID/<START_ID>/SEQ/<N>/MACROVERSION/<VERSION>/PROGRAM/817426]

(ALL COUNTED MACHINING FOR ONE PHYSICAL CYCLE)

DPRNT[MEIMAD/V/1/EVENT/CEN/ID/<END_ID>/SEQ/<N_PLUS_1>/MACROVERSION/<VERSION>/PROGRAM/817426]

M30
```

## 12. Easy CST/CEN rules

Follow these rules if cycle events are enabled:

1. Write one CST when one physical cycle starts.
2. Write one CEN only when that same cycle finishes successfully.
3. The CEN sequence number must equal the CST number plus one.
4. Do not write CEN after an alarm, Reset, E-stop, failed probe, failed tool check, or operator abort.
5. Do not put CEN in a shared footer that an error path can reach.
6. For a multi-output NC cycle, write one CST/CEN pair for the complete cycle, not one pair for every part output.
7. Do not try to repair missing or duplicated events in the post.

The safest source structure is to call `writeCycleEnd()` only from the one branch that proves the whole cycle completed.

Pseudocode:

```text
if cycleDprntEnabled:
    prepare approved sequence N
    write CST using N

write normal machining
run all required completion checks

if all work completed successfully and cycleDprntEnabled:
    write CEN using N + 1

write M30
```

## 13. DPRNT text rules

The Server expects the fields in this exact order:

```text
MEIMAD/V/1/EVENT/<CODE>/ID/<ID>/SEQ/<NUMBER>/MACROVERSION/<NUMBER>/PROGRAM/<NC_ID>
```

Keep it simple:

- Use uppercase letters.
- Do not add spaces inside the message.
- Use `/` between fields.
- Keep the field order exactly as shown.
- Use only uppercase letters, digits, and hyphens in IDs.
- Keep each complete message at or below 512 bytes.
- Use a different event ID for every new event.
- Use the same six-digit NC ID in `PROGRAM`.

Wrong—spaces and field order changed:

```gcode
DPRNT[MEIMAD / EVENT / CST / V / 1 / SEQ / 201]
```

Correct shape:

```gcode
DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-817426-S-201/SEQ/201/MACROVERSION/5/PROGRAM/817426]
```

## 14. What not to put in the post

Do not add any of these items:

- Machine secret or key;
- verification response calculation;
- nonce calculation;
- protected macro variable numbers;
- O9001, O9002, or O9003 program bodies;
- direct OLC, SVS, or SVF DPRNT output;
- Offset Loader token in the normal machining file;
- automatic bypass when verification fails;
- CST/CEN before the sequence method is approved.

The post calls the protected macros. It does not replace them.

## 15. Common errors

| Error | Fix |
|---|---|
| Verification line is after `G90` or another G-code | Move it before every executable block. |
| Two hooks appear | Call the hook-writing function only once from the main program opening code. |
| NC ID has five digits | Stop generation and request a value from 100000 to 999999. |
| `A817426` has no decimal point | Output `A817426.`. |
| Extra `B` value appears on the O9002 line | Remove it. O9002 receives only the NC ID in `A`. |
| O9001 call appears in the machining program | Move it to the separate Offset Loader generator. |
| Post prints SVS after the hook | Remove it. O9002 owns SVS/SVF output. |
| CEN is written before `M30` on every path | Move it to the successful completion branch only. |
| Same NC ID is used for a changed release | Generate a new ID and regenerate the file. |

Meimad may report these release errors:

| Meimad error | Meaning |
|---|---|
| `verification_hook_required` | The file has no hook. |
| `verification_hook_not_first` | Executable code appears before the hook. |
| `verification_hook_ambiguous` | The marker appears more than once. |
| `verification_hook_invalid` | The syntax, program number, or ID is wrong. |
| `verification_identity_reused` | The NC ID was already used. Generate a new one. |

## 16. Final programmer checklist

Before delivering the post, check:

- [ ] The file writes `PART`, `CASE`, `OPERATION`, and `REVISION` comments.
- [ ] Exactly one O9002 verification call is present.
- [ ] It is the first executable line.
- [ ] The NC ID contains six digits and ends with a decimal point in the `A` argument.
- [ ] The fixed marker is `(MEIMAD VERIFY V1)`.
- [ ] Normal Haas safety codes come after the hook.
- [ ] The normal post does not contain OLC, SVS, or SVF DPRNT lines.
- [ ] The normal post does not contain an O9001 Offset Loader call.
- [ ] CST/CEN is disabled unless Meimad approved all required inputs.
- [ ] If enabled, CEN can run only after one complete successful cycle.
- [ ] A changed NC release gets a new six-digit NC ID.
- [ ] A no-motion test file was generated and published successfully.

## 17. Copy/paste quick reference

Main machining program—write once as the first executable line:

```gcode
G65 P<VERIFY_O9XXX> A<NEW_NC_ID>. (MEIMAD VERIFY V1)
```

Separate Offset Loader—write after successful offset readback:

```gcode
G65 P<CHALLENGE_O9XXX> A<OFFSET_TOKEN>. B<NC_ID>.
```

Optional commissioned cycle start:

```gcode
DPRNT[MEIMAD/V/1/EVENT/CST/ID/<START_ID>/SEQ/<N>/MACROVERSION/<VERSION>/PROGRAM/<NC_ID>]
```

Optional commissioned cycle end:

```gcode
DPRNT[MEIMAD/V/1/EVENT/CEN/ID/<END_ID>/SEQ/<N_PLUS_1>/MACROVERSION/<VERSION>/PROGRAM/<NC_ID>]
```

Do not write in the normal post:

```text
OLC  SVS  SVF
```

## 18. Quick postprocessor examples

These are short integration templates for a post writer. They are not complete vendor posts. Keep the existing post's file-opening, validation, formatting, and error functions, and adapt only the marked Meimad output point.

### SolidCAM GPPL - one main NC file

Add one call in the procedure that opens the main NC file. The call must be after comments and before the first normal Haas code:

```text
@start_of_file

    call @usr_sof_character
    call @usr_sof_progname
    call @usr_sof_commentsbeforecodes

    ; MEIMAD: exactly once per generated main NC file.
    call @usr_meimad_verification

    call @usr_sof_gmcodes
    call @usr_sof_commentsaftercodes

endp


@usr_meimad_verification

    if iVMID_MEIMAD_ENABLED eq 1
        {nl,'G65 P'iVMID_MEIMAD_VERIFY_PROGRAM' A'iVMID_MEIMAD_NC_ID'. (MEIMAD VERIFY V1)'}
    endif

endp
```

Example inputs are `iVMID_MEIMAD_ENABLED = 1`, `iVMID_MEIMAD_VERIFY_PROGRAM = 9002`, and `iVMID_MEIMAD_NC_ID = 817426`. The final posted line must be:

```gcode
G65 P9002 A817426. (MEIMAD VERIFY V1)
```

If the SolidCAM project creates two separate main NC files, call the routine once in each file-opening path and supply a different new six-digit NC ID for each file. Do not call it once per CAM job or tool.

### Cimatron original GPP - one main NC file

Declare the input with the other post interactions, then write it inside the existing `BEGINNING OF TAPE` block:

```text
FORMAT (SEQUENCING) MEIMAD_NC_ID ;

INTERACTION (SEQUENCING)
    "MEIMAD NC ID - SIX DIGITS"
    MEIMAD_NC_ID = 0 ;

BEGINNING OF TAPE:

    OUTPUT "% " \J "O" PGN ;
    * Existing full-line header comments go here.

    * MEIMAD: this is the first executable block.
    OUTPUT $ " G65 P9002 A" MEIMAD_NC_ID
        ". (MEIMAD VERIFY V1)" ;

    * Existing Haas initialization follows.
    OUTPUT $ " G17 G40 G49 G80 G90" ;
```

Use the customer's existing GPP stop/error pattern to reject any value outside `100000` through `999999`. Confirm that the local `SEQUENCING` format does not add a sign, grouping separator, or decimal digits.

### Cimatron GPP2 - validation and output

This compact GPP2 example stops posting when the NC ID is invalid, then writes the hook before ordinary Haas initialization:

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
    // Existing full-line header comments go here.

    OUTPUT $ " G65 P9002 A" MEIMAD_NC_ID
        ". (MEIMAD VERIFY V1)" ;

    OUTPUT $ " G17 G40 G49 G80 G90" ;
```

For either Cimatron system, every split main output file needs one hook and its own new NC ID. If the post has only one NC ID input but produces several files, stop the post or disable split output for that release.

### Expected generated NC from all three examples

```gcode
%
O01995 (POST OUTPUT TEST)
(PART: TESTSERVERVERIFICATION005)
(OPERATION: 10 TEST1)
G65 P9002 A817426. (MEIMAD VERIFY V1)
G17 G40 G49 G80 G90
M30
%
```

The sample ID `817426` is for documentation only. Assign a new six-digit NC ID before every real Meimad release.

## 19. References

- [Meimad API contract](api-contract.md)
- [Production Run architecture](production-run-architecture.md)
- [SolidCAM postprocessor overview](https://www.solidcam.com/highlights/postprocessors)
- [Cimatron GPP introduction](https://help.cimatron.com/en/2026/Introduction_to_GPP.htm)
- [Cimatron GPP/GPP2 Post Process dialog](https://help.cimatron.com/en/2026/post_processor/post_gpp.htm)
- [Cimatron GPP post file structure](https://help.cimatron.com/en/2026/Post_Processor_Program_File_Structure.htm)
- [Cimatron GPP2 OUTPUT statement](https://help.cimatron.com/en/2026/OUTPUT.htm)
- [Haas macro documentation](https://www.haascnc.com/service/online-operator-s-manuals/mill-operator-s-manual/mill---macros.html)
- [Haas DPRNT documentation](https://www.haascnc.com/service/troubleshooting-and-how-to/how-to/communication-with-external-devices---dprnt.html)
- [Haas Setting 23](https://www.haascnc.com/service/codes-settings.type%3Dsetting.machine%3Dmill.value%3DS23.html)
