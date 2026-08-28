# Haas VF-3SS protected verification - internal engineering decision record

## Purpose

This worksheet is for a written review by the site's qualified CNC controls
engineer and the Meimad production owner before the separately generated macro-v6
bench candidate may be loaded for its bounded no-motion retest. External HFO/vendor approval is explicitly
not required. This is not an executable macro package and does not authorize
another CNC test. The affected control is a VF-3SS running NGC `100.21.000.1001`.

Suggested review subject: `VF-3SS NGC M109 post-input timer barrier and persistent evidence counter`

Suggested cover note:

> Please review the timer/input and sequence decisions in this document for our
> exact VF-3SS/NGC version. This is a no-motion setup-verification interlock. We
> will not generate or load another candidate until the internal reviewers select
> and document a supportable post-M109 execution barrier/input pattern and an
> evidence-sequence design. The counter would never control Machine workflow.

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

## Questions requiring a written engineering decision

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

The no-motion procedures and generated controller probes for questions 1-6 are
defined in [Haas NGC engineering behavior tests](haas-ngc-engineering-machine-tests.md).
They require exact physical observations; source inspection is not an answer.

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

invoke the engineer-approved execution barrier/finalizer

inside the finalizer only:
    blank barrier blocks if required by the approved review
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

Implemented v6 candidate design for review: reserve one additional protected persistent variable
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

Implementation of the bench candidate is not approval. The internal engineering
answer and product decision must be recorded before controller loading or any
commissioning-checklist acceptance change.

## Acceptance plan after review

1. Record the written internal engineering answer and approve one sequence design.
2. Review the already generated, visibly bench-only macro-v6 candidate and its
   hashes without editing it at the Machine.
3. Add structural tests for the approved M109/barrier pattern and one sequence
   domain across every emitted event type.
4. Run all Server, client, package, parser, and simulator tests.
5. Rehearse the complete event flow using the loopback simulator.
6. Prepare one bounded no-motion physical script with explicit pass/fail stops.
7. Keep protected verification disabled unless every commissioning gate and both
   sign-offs pass.

## Written response and decision record

Review source: `scripts/new-haas-verification-v6-bench-pack.ps1`. Its manifest is
`BENCH_ONLY_INTERNAL_REVIEW_REQUIRED`; it requires three protected programs and
five collision-free variables, uses a separate protected G65 finalizer after the
M109 loop, repeats NC/release/nonce correlation on SVS/SVF, and fails closed
instead of wrapping the counter at sequence exhaustion. These statements describe
the candidate source, not physical acceptance.

Complete this section from the written internal review. A verbal answer or an
unreferenced sample is insufficient.

| Field | Recorded answer |
|---|---|
| Qualified CNC controls engineer / organization | |
| Internal case or work-order reference | |
| Response date | |
| Exact Machine / NGC version covered | |
| Supported post-M109 fresh-read pattern | |
| Required G103/blank-block placement | |
| Reset / E-stop / Single Block / Block Delete constraints | |
| `#3001` assignment and wrap behavior | |
| Protected persistent evidence counter supported | `YES` / `NO` / qualified answer |
| Approved persistent variable range and collision process | |
| Controller documentation / validated reference | |

Product sequence-design decision:

- [x] `PERSISTENT_COUNTER`: one protected, increment-only evidence sequence shared
  by OLC, SVS, SVF, CST, and CEN; never workflow authority.
- [ ] `EXPLICIT_EPOCH`: Server protocol/schema change with an unambiguous epoch
  source and buffered-event rule.
- [ ] Neither design is approved; protected CNC verification remains abandoned
  or deferred.

Chosen decision, approver, date, and rationale:

```text
PERSISTENT_COUNTER selected by explicit Meimad owner direction on 2026-08-28.
Use one configured protected persistent variable, initialize it once to the
recorded positive value 1, increment it for every OLC/SVS/SVF/CST/CEN event, and
fail closed when unset, invalid, decreased, or exhausted. Never wrap or silently
reseed it. This is a product design selection, not evidence that #10504 is
collision-free or persistent on the VF-3SS; those facts remain subject to the
real-Machine tests and named sign-off.
```

Only one choice may be selected. After approval, link the answer and decision from
the commissioning checklist and use the bounded retest document; do not reuse any
v3-v5 candidate.
