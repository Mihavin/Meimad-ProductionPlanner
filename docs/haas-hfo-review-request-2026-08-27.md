# Haas VF-3SS protected verification - HFO review request

## Purpose

This request is for a Haas Factory Outlet review before Meimad generates another
protected-macro commissioning candidate. It is not an executable macro package and
does not authorize another CNC test. The affected control is a VF-3SS running NGC
`100.21.000.1001`.

## Observed failure

The quarantined macro-v5 candidate used `G103 P1`, captured `#3001` before six
`M109` digit prompts, and placed a second `#3001` read and a 120-second check after
the sixth prompt in source order. During a no-motion physical test, the operator
waited at least 130 seconds at the first prompt, entered the otherwise correct
response, and the macro returned without the expected timeout alarm.

The v5 source therefore did not prove that the final timer expression executed
after operator input. Server-side expiration rejected the late result, but that
cannot replace a CNC-side interlock before machining.

## Haas documentation applied

- Haas M109 examples clear the input variable and loop after `M109` until the
  variable becomes nonzero.
- Haas documents that macro expressions execute during look-ahead.
- `G103 P1` limits interpretation to one block ahead; it does not eliminate it.
- Haas recommends several empty blocks after `G103 P1` when the following macro
  statement must not be interpreted early.
- Haas defines `#3001` as milliseconds since power-on.

References:

- [M109 Interactive User Input](https://www.haascnc.com/service/codes-settings.type%3Dmcode.machine%3Dmill.value%3DM109.html)
- [G103 Limit Block Look-Ahead](https://www.haascnc.com/service/codes-settings.type%3Dgcode.machine%3Dmill.value%3DG103.html)
- [Haas Mill Macros](https://www.haascnc.com/service/online-operator-s-manuals/mill-operator-s-manual/mill---macros.html)
- [Haas Mill Programming Workbook](https://www.haascnc.com/content/dam/haascnc/en/service/reference/programming-workbooks/mill---programming-workbook.pdf)

## Questions requiring written HFO confirmation

1. On NGC `100.21.000.1001`, what documented block pattern guarantees that a
   `#3001` expression is evaluated only after `M109` has accepted a character?
2. Is the official `M109` nonzero loop, combined with `G103 P1` and blank blocks,
   a sufficient execution barrier for a fresh post-input timer read?
3. If not, is a protected `M97`, `M98`, or `G65` finalizer call a supported barrier,
   and where must `G103 P1` and blank blocks be placed around that call?
4. Can Reset, E-stop, Single Block, Block Delete, or a mode change allow execution
   to pass the finalizer or alarm path?
5. Does `#3001` wrap or permit assignment on this NGC version, and what behavior
   should a macro expect at the wrap boundary?
6. Is a dedicated protected persistent variable suitable as an increment-only
   event counter when it is evidence only and never controls machine workflow?

## Non-executable candidate structure for review

The following is pseudocode. Program numbers, variable numbers, labels, and alarm
numbers are deliberately omitted so it cannot be mistaken for a loadable package.

```text
limit look-ahead to one block
blank barrier blocks recommended by Haas
capture start timer

for each response digit:
    clear input variable
input_label:
    request one character with M109
    if input variable is zero, return to input_label
    reject a non-digit
    consume the digit

invoke the HFO-approved execution barrier/finalizer

inside the finalizer only:
    blank barrier blocks if required by HFO
    capture a fresh end timer
    fail closed if end timer is earlier than start timer
    fail closed if elapsed time exceeds the configured limit
    compare the complete response
    emit success only after every check passes
```

The final candidate must alarm before returning to the calling NC program on every
timeout, invalid input, Reset-recovery, expired challenge, or response mismatch.
Static source ordering is not acceptance evidence.

## Event-sequence contract requiring product approval

The quarantined package uses different sequence domains: verification events use
`#3001`, while cycle events use a value derived from the parts counter. The Server
currently treats all Haas DPRNT workflow events for one Machine as one monotonic
source. Those domains cannot safely be mixed, and `#3001` also restarts after power
cycling.

Preferred design for review: reserve one additional protected persistent variable
as an increment-only event counter shared by OLC, SVS, SVF, cycle-start, and
cycle-end emission. It is evidence only: it cannot start, stop, reorder, verify,
or otherwise control workflow. The Server remains authoritative. Exact replay is
still deduplicated by the immutable event ID. A missing, cleared, decreased,
non-integer, exhausted, or unapproved counter must fail closed and create an
operator-visible recovery requirement; it must never be silently repaired.

Alternative design: introduce an explicit Server connection epoch and scope
sequence checks to that epoch. This is more complex because reconnects, Server
restarts, controller reboots, and events buffered across a disconnect must be
distinguished without trusting network timing. It should be selected only if Haas
does not approve the protected counter.

No implementation choice is approved by this document. The HFO answer and product
decision must be recorded before changing the generator, schema, ingestion source,
or commissioning checklist.

## Acceptance plan after review

1. Record the written HFO answer and approve one sequence design.
2. Generate exactly one new, visibly bench-only candidate.
3. Add structural tests for the approved M109/barrier pattern and one sequence
   domain across every emitted event type.
4. Run all Server, client, package, parser, and simulator tests.
5. Rehearse the complete event flow using the loopback simulator.
6. Prepare one bounded no-motion physical script with explicit pass/fail stops.
7. Keep protected verification disabled unless every commissioning gate and both
   sign-offs pass.
