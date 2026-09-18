# Postprocessor writer documentation audit — 2026-09-18

**Scope:** the documents handed to CAM postprocessor writers and CNC controls reviewers —
`postprocessor-production-package-contract.md` (authoritative contract), the Haas-only
`haas-ngc-postprocessor-and-macro-specification.md` with its Word rendering
`Meimad-Haas-NGC-Postprocessor-Programmer-Guide-v1.7.docx`, the unimplemented
`...v2-draft.md`, and the guide build script — audited against what
`NcPackagePlaceholderSchema`, `NcPackageTemplateTransformer`, `ProductionPackageService`,
`HaasDprintProtocol`, `CncDprintEventIngestionService`, `CncVerificationFoundationService`,
and the DPRNT readers actually do, and against the request to cover Mazak, Haas, FANUC
0i/30i/31i, and Okuma OSP-P200 controls.

**Result:** the placeholder contract itself is control-neutral and correctly documented,
but the writer guide was Haas-only, one of its DPRNT examples never worked, and the code
the Server injects into runnable NC is Haas-dialect throughout. The guide is replaced by a
controller-neutral specification with per-control examples; two implementation gaps that
the FANUC example depends on are fixed in this change; the dialect gap is recorded as
open decisions OD-036 and OD-037 rather than silently worked around.

## Documents and their fate

| Document | Before | After |
|---|---|---|
| `docs/postprocessor-production-package-contract.md` | Authoritative; DPRNT example `DPRNT[PART=...]` never matched the reader | Example corrected; ownership rules unchanged |
| `docs/haas-ngc-postprocessor-and-macro-specification.md` | Live writer guide, Haas NGC only | Replaced by `docs/nc-postprocessor-and-macro-specification.md` (Haas NGC, FANUC 0i/30i/31i, Mazak Matrix, Okuma OSP-P200) |
| `docs/Meimad-Haas-NGC-Postprocessor-Programmer-Guide-v1.7.docx` | Word rendering of the Haas guide; README linked a v1.5 file that no longer existed | Regenerated as `Meimad-NC-Postprocessor-Programmer-Guide-v1.8.docx`; v1.7 moved to `.diagnostics/obsolete-documents/` (git-ignored) following the v1.6 precedent; README link fixed |
| `docs/haas-ngc-postprocessor-and-macro-specification.v2-draft.md` | Unimplemented draft with a standing warning | Kept; its links updated to the new live document. Delete it once OD-036 supersedes its design |
| `scripts/build-haas-postprocessor-guide-docx.ps1` | Hard-coded Haas title, header, and SolidCAM/Cimatron-only subtitle | Renamed `build-postprocessor-guide-docx.ps1`; neutral title/subtitle; new default paths |

## Findings

Severity reflects the effect on a writer following the document as written.

### F1 (high) — Coverage: one control documented, four in the factory

The only writer-facing guide named Haas NGC in its title, header, and every example; there
was no Mazak, FANUC, or Okuma guidance anywhere in `docs/`, although the connection
platform now offers Haas MDC/MTConnect, DPRNT-only (Mazak), and FANUC FOCAS connection
types and the factory runs Mazak Variaxis (Matrix 2 and Smooth), FANUC-controlled
machines, and an Okuma Genos L200E-M. **Fixed:** new controller-neutral specification with
a per-control chapter (section 6) and a support matrix (section 4).

### F2 (high) — Everything the Server injects is Haas dialect

`NcPackageTemplateTransformer.AppendCycle` emits `G103 P1`/`G103 P0` (Haas look-ahead
control; FANUC/Mazak raise an improper-G-code alarm), and `CncVerificationFoundationService`
validates nonce/state/release/sequence variables to `#10000–#10999` and the response
variable to the Haas M109 ranges `#500–#549`/`#10500–#10549`. FANUC and Mazak custom
macro B persist `#500–#999` only; Okuma has neither `#` variables nor `G65`. So
verification, the Offset Loader handshake, and part counting are Haas-only by
construction, and a verification configuration for a non-Haas Machine cannot even be saved.
**Fixed the same day** (see the addendum at the end): schema v76 adds the Machine `ncDialect`, the
transformer renders every injected block per dialect, and the verification ranges are validated per dialect.

### F3 (high) — `EVENT_CONTEXT` always becomes a DPRNT, and the Server never parses it

`TransformCanonical` expands `EVENT_CONTEXT` into `(MEIMAD EVENT CONTEXT V2)` plus
`DPRNT[MEIMAD/V/2/CONTEXT/...]` regardless of the verification policy. Two consequences:
an Okuma OSP Machine (no DPRNT) cannot receive a runnable package at all today, even
verification-disabled; and because `HaasDprintProtocol.TryParse` requires `V=1` and the
`EVENT` field, every ingestion of that line is rejected as `invalid_field_order` and
`CncDprintEventIngestionService` logs "Rejected malformed CNC DPRINT event" at each program
start. The line is harmless correlation evidence in the raw log. **Documented** in the
specification (sections 4 and 5); the Okuma gap is closed by the `OKUMA_OSP` dialect (addendum); downgrading the
log for `V/2/CONTEXT` lines is a recommended follow-up.

### F4 (high) — The documented DPRNT PartName line never matched

Both the contract and the guide showed `DPRNT[PART=[[MEIMAD:PART_NAME]]]` and
`DPRNT[OP=[[MEIMAD:OPERATION_NAME]]]`. `HaasDprntPartReader.TryParsePartName` accepts only
a bare part-number-shaped line (`^[A-Z0-9]+(?:[-.][A-Z0-9]+)+$`, at least one digit, not
ending `.CNC`); `PART=30P647004101-001` is rejected, so the "direct control-authored
PartName" path described in the architecture could not have worked from a template that
followed the guide. **Fixed** in both documents: `DPRNT[[[MEIMAD:PART_NAME]]]`, with the
shape rule spelled out. Regression tests now assert that `PART=...` and separator-less
`ABC123` are rejected. Follow-up question for the product owner: Part numbers without a
`-`/`.` separator are never recognized on the DPRNT path; if any exist, the shape rule
must be relaxed server-side.

### F5 (medium) — FANUC `POPEN`/`PCLOS` control codes broke the first and last line

FANUC sends DC2 (0x12) with `POPEN` and DC4 (0x14) with `PCLOS`. The readers only
trimmed whitespace, so `\x12` + `30P647...` was not part-number-shaped and
`\x12MEIMAD/...` did not start with `MEIMAD/`: the PartName line and the first event
after program start would have been silently dropped on every FANUC Machine. **Fixed:**
`HaasDprntPartReader.StripControlCharacters` is applied in the TCP and FILE readers
(tabs kept), with unit tests for DC2, DC4, NUL padding, and DEL.

### F6 (medium) — A serial-to-Ethernet bridge could not be addressed

The TCP DPRNT source always connected to the controller's own IP; `dprnt` carried a `port`
override but no host. A FANUC (or Okuma) whose RS-232 port feeds a bridge with its own
address had no working configuration. **Fixed:** `dprnt.host` on the generic CNC
configuration, validated as an IP or host name, used by `CncDprntSource`, exposed in the
FANUC FOCAS Setup panel as "DPRNT TCP host", documented in the API contract and user help,
with server, API, client-API, and view-model tests.

### F7 (medium) — Legacy Haas saves discard generic-only DPRNT fields

`SqliteHaasIntegrationRepository.UpsertGenericConnectionAsync` rebuilds `configuration_json`
from the legacy Haas settings record, which carries `dprntSource`, `dprntFilePath`, and
`dprntFileClearPolicy` but not `dprnt.port`/`dprnt.host`. A Haas connection edited through
the generic endpoint would lose those two on the next legacy save. **Documented** as
FOCAS-only fields; no Haas Machine uses a bridge today.

### F8 (low) — README linked a guide that did not exist

`README.md` pointed at `Meimad-Haas-NGC-Postprocessor-Programmer-Guide-v1.5.docx`; only
v1.7 existed in `docs/`. **Fixed** (link to the v1.8 guide and the new specification).

### F9 (low) — Build script and headers hard-coded the Haas scope

`build-haas-postprocessor-guide-docx.ps1` wrote "HAAS NGC POSTPROCESSOR GUIDE" in the page
header and a SolidCAM/Cimatron-only subtitle. **Fixed** by renaming the script and making
the title neutral; the rendering logic is unchanged.

### F10 (low) — DPRNT character set is not enforced at package build

`NcText` replaces only non-printable characters, brackets, and parentheses with `_`. A Part
number with lowercase letters, spaces, or `_` survives into `DPRNT[...]`, where FANUC
either drops or mangles it and where the reader would reject it anyway. **Documented** in
the specification (section 2). Recommended follow-up: warn at package build when a value
placed inside a DPRNT is not DPRNT-safe.

### F11 (info) — Okuma transfer header defeats hook-position validation

An OSP program exported with its `$NAME.MIN%` first line is classified as executable by
`NcPackagePlaceholderSchema.IsHeaderOrCommentOrPlaceholder`, so the hook can never
"precede the first executable block". **Documented** (release without that line); header
recognition is part of OD-037.

### F12 (info) — Look-ahead barrier differs per control

Haas uses `G103 P1` so the DPRNT executes in program order. FANUC needs a non-buffered
M-code (parameters 3411–3420) or equivalent; Mazak and Okuma equivalents are unknown.
Part of OD-036.

## Controller coverage after this audit

| Control | Writer guidance | Package buildable | Verification / part counting | Server connection |
|---|---|---|---|---|
| Haas NGC | Complete (sections 1–5, 6.1) | Yes | Implemented, bench-commissioning gated | MDC / MTConnect |
| FANUC 0i-D/F, 30i/31i/32i-B | Complete for header, DPRNT via `POPEN`/`PCLOS`, bridge, parameters (typical values, to be confirmed per control) | Yes (`FANUC_MACRO_B`) | Rendered per dialect; O9001–O9003 subprograms still to be written and commissioned | FANUC FOCAS |
| Mazak Matrix / Matrix 2 | Complete for header, DPRNT to `print.txt` (DPR14 = 4), FILE source | Yes (`MAZAK_MATRIX_EIA`) | Rendered per dialect; subprograms still to be ported and commissioned | DPRNT only |
| Mazak Smooth | Path/parameter marked unverified | Yes (`MAZAK_MATRIX_EIA`) | As Matrix; the Smooth DPRNT file parameter is unverified | DPRNT only |
| Okuma OSP-P200/P300 | Complete (section 6.4): `CALL`/`PUT`/`WRITE C` dialect | Yes (`OKUMA_OSP`) | Rendered per dialect; `PUT`/`WRITE` device routing and subprograms to commission (OD-037) | DPRNT only (TCP bridge or FILE) |

## Changes made in this audit

- New `docs/nc-postprocessor-and-macro-specification.md`; old Haas-only specification
  removed; `README.md`, the v2 draft banner, and the build script now point to it.
- `docs/postprocessor-production-package-contract.md`: DPRNT example corrected (F4).
- `docs/implementation-plan.md`: OD-036 and OD-037 added; status note for F5/F6.
- `docs/api-contract.md`, `docs/architecture.md`, `docs/user-help.md`: `dprnt.host`,
  PartName shape rule, control-code stripping, new troubleshooting entries.
- Server: control-code stripping in both DPRNT readers; `dprnt.host` with validation.
- Windows client: "DPRNT TCP host" field in the shared DPRNT section.
- Tests: reader stripping (TCP helper and FILE reader), rejected `PART=`/`ABC123`
  lines, bridge host on `CncDprntSource`, FOCAS API round trip and rejection of an invalid
  host, client API serialization, and Setup view-model save.
- Word guide v1.8 regenerated from the new specification.

## Recommended follow-ups (not done here)

1. OD-036 was decided and implemented the same day (addendum); what remains is commissioning the non-Haas controls.
2. Verify on the OSP-P200L which event-output mechanism exists (User Task 2 to RS-232, or
   a Windows-side file) and record it in OD-037.
3. Confirm the FANUC parameter numbers in section 6.2 against the parameter manual of each
   control series actually installed, and the Mazak Smooth DPRNT file parameter.
4. Downgrade or special-case the ingestion log for `MEIMAD/V/2/CONTEXT` lines (F3).
5. Add a package-build warning for DPRNT-unsafe Part numbers (F10) and decide whether
   separator-less Part numbers must be accepted (F4).
6. Delete the v2 draft once its useful ideas are folded into OD-036.

## Addendum (2026-09-18, later the same day): NC dialect implemented

The owner decided OD-036 immediately: Okuma OSP, FANUC, and Mazak all print data and
call subprograms, only in their own syntax and variable ranges, so the Server now renders
per control instead of documenting a Haas-only limitation.

- Schema v76 adds `machines.nc_dialect` (`HAAS_NGC` default, `FANUC_MACRO_B`,
  `MAZAK_MATRIX_EIA`, `OKUMA_OSP`), exposed as `ncDialect` on the Machine API and as
  "NC dialect" in Setup. A change is refused while the Machine's verification is enabled.
- `Application/GCode/NcDialects.cs` renders the verification hook, event context,
  cycle-event blocks, and Offset Loader per dialect. F2 and F12 are closed for FANUC and
  Mazak (same macro B, no `G103`, `#500–#999`); F3 is closed for Okuma (`CALL O9002 PA=`,
  `PUT '...'` + `WRITE C`, `VC1–VC200`, `IF [...] Nlabel`/`GOTO`, Offset Loader `O1990.MIN`
  ending in `M02`).
- Verification variable ranges are validated per dialect in the service and in the v76
  SQLite triggers, so a mapping can never be stored for the wrong control.
- The specification's SolidCAM and Cimatron examples were rewritten in real GPPL
  (`@start_program` … `output "..."` … `endp`) and GPP `.exf` (`BEGINNING OF PROGRAM:` …
  `OUTPUT \J "..." ;`) syntax; the earlier examples used an invented pseudo-syntax.
- Server tests cover the transformer per dialect, the Okuma package end to end, the
  verification ranges per dialect, the v76 triggers, and the Machine API dialect rules.
- Still open: the exact OSP `PUT`/`WRITE` statement form and output device on the
  installed OSP-P200L (OD-037), `$NAME.MIN%` header recognition (F11), an optional FANUC
  non-buffered M-code barrier (F12), the protected O9001–O9003 subprograms for every
  non-Haas control, and the `MEIMAD/V/2/CONTEXT` ingestion log noise (follow-up 4).
