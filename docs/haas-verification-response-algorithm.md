# Haas setup-verification response algorithm

## Status

Algorithm v1 is an implemented **reference and physically matched arithmetic candidate**, not commissioned production behavior. On 2026-08-26 the VF-3SS reproduced every published vector, including leading-zero response `0282`. Schema v52 retains the validated six-digit nonce in a pending one-time Server session; the authenticated assigned-tablet status projection derives the fixed-width response in memory, and strict protected-macro success/failure DPRINT events resolve that current session and drive the Server workflow. CNC-side production input, alarms, cleanup, protected-key behavior, and the physical cutting interlock remain uncommissioned.

The algorithm is deliberately controller-friendly rather than cryptographically strong. Its purpose is freshness and replay resistance within a protected setup handshake. Network security, protected-program access, Server authorization, short expiration, and one-time session consumption remain separate controls.

## Inputs

All four calculation inputs are integers from `100000` through `999999`:

1. fresh nonce generated for the current Offset Loader execution;
2. current Offset Loader release token;
3. immutable NC identity from the generic first-block hook;
4. per-Machine numeric key provisioned only inside the protected program.

The response width is configured as four, five, or six decimal digits. Leading zeroes are significant for display and entry.

The Server derives the six-digit Machine key from the encrypted Machine verification secret without returning the result through an API:

```text
digest = HMAC-SHA-256(
    key  = UTF-8 verification secret,
    data = UTF-8 "MEIMAD-CNC-VERIFY-V1\0" + trimmed stable Machine ID)

machine_key = 100000 + (big-endian uint32(digest[0..4]) mod 900000)
```

An authorized offline commissioning process must place only this derived numeric key as a literal inside the protected Machine-specific macro. Rotating the Server secret therefore requires a controlled protected-macro reprovision and a new physical vector check. Neither the original secret nor the derived key belongs in tablet payloads, normal diagnostics, DPRINT, source control, or command-line history.

## Decimal fold

Start with `state = 7919`, then fold these symbols in order:

```text
algorithm version digit: 1
six nonce digits, most significant first
six Offset Loader token digits, most significant first
six NC identity digits, most significant first
six Machine-key digits, most significant first
finalization digits: 3, 1, 4, 1, 5, 9
```

For every symbol:

```text
state = (state mod 90909) * 11 + symbol
```

The CNC candidate may express the same reduction without a `MOD` operator:

```text
remainder = state - FIX[state / 90909] * 90909
state = remainder * 11 + symbol
```

Every state and intermediate remains between zero and `999998`. This avoids relying on large integers, but it does not eliminate Haas floating-point round-off risk. The protected implementation must apply the HFO-approved integer normalization, use `ROUND` for comparisons where required, limit look-ahead as commissioned, and match the reference vectors on the installed controller software.

The final response is:

```text
response = state mod 10^configured_digits
```

It is formatted with leading zeroes to the configured width. The control compares the numeric value, while the tablet shows the fixed-width string.

## Published independent vectors

The numeric key `271828` is public test data and must never be used as a production key.

| Nonce | Offset token | NC identity | Test Machine key | Width | Response |
|---:|---:|---:|---:|---:|---:|
| 731841 | 483920 | 654321 | 271828 | 6 | `438513` |
| 731842 | 483920 | 654321 | 271828 | 6 | `286999` |
| 731841 | 483921 | 654321 | 271828 | 6 | `543409` |
| 731841 | 483920 | 654322 | 271828 | 6 | `953665` |
| 731841 | 483920 | 654321 | 271829 | 6 | `210076` |
| 100000 | 100000 | 100000 | 100000 | 4 | `0282` |
| 999999 | 999999 | 999999 | 999999 | 5 | `69667` |

The standalone calculator is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\haas-verification-vector.ps1 `
  -Nonce 731841 `
  -OffsetReleaseToken 483920 `
  -NcIdentityToken 654321 `
  -TestMachineKey 271828 `
  -ResponseDigits 6
```

It is for public test keys only. Production key derivation/provisioning must not use command-line arguments.

For passive CNC capture, the no-motion bench macro emits each result in this exact strict format:

```text
MEIMADSPIKE/V/1/TEST/V01/NONCE/731841/OFFSETRELEASE/483920/NC/654321/DIGITS/6/RESPONSE/438513
```

`V01` through `V07` correspond to the table order. The line contains no Machine key. `scripts/haas-verification-spike.ps1` remains read-only/passive and now grades these captured lines into `responseVectorGrade`; missing, duplicate, malformed, unexpected, or mismatched attempted vectors make the status `FAIL`. Blank transport records and unrelated non-`MEIMADSPIKE` lines are ignored, while any malformed attempted `MEIMADSPIKE` record still fails closed. A capture containing only the separate `MEIMADSPIKE/CASE/...` identity probes reports vector status `NOT_RUN`, not a vector failure. Captured `MEIMADSPIKE` lines are evidence only and are never ingested as operational workflow events. An existing capture or exported line file can be graded independently with `scripts/haas-verification-grade.ps1`.

The same capture tool grades the direct/nested commissioning pack into `identityTransportGrade`. Cases `1`, `2`, and `4` must each deliver their exact expected supplied identity at least four times; malformed/mismatched lines or insufficient repetitions fail the grade. Four was accepted as the intentional physical sample size on 2026-08-26. This grade proves transport only, never an intrinsic caller identity or production interlock.

## Protected-program layout candidate

The commissioning pack now includes O09012/O09013 as a no-motion, public-key vector candidate. It is not production macro code and still requires HFO review plus physical proof of program numbers, temporary variable `#10500`, DPRNT behavior, `FIX` arithmetic, Reset cleanup, and access protection on the actual control.

- The Offset Loader calls the configured protected challenge program only after every offset write succeeds. That program first invalidates prior success, establishes a fresh six-digit nonce, retains the current six-digit Offset Loader token, and emits the strict `OLC` DPRINT.
- The approved NC file calls the configured protected verification program as its first executable block and passes its six-digit identity through `A...`; Haas documents that `A` maps to local variable `#1`. The decimal point is mandatory in the Meimad hook so the six-digit value is not scaled as an integer macro argument.
- If the same nonce, Offset Loader token, and NC identity are already verified, the macro may return. Otherwise it keeps verification invalid, validates the challenge age, copies the nonce and release token into G65-local variables, and clears the persistent validity marker and challenge variables before the first operator-input prompt. It then obtains the operator's tablet-displayed response through the commissioned input method, calculates v1 independently, clears each entered digit, and rechecks the challenge age after the final digit and before comparison. It either raises the commissioned `#3000` failure alarm or emits `SVS` and permits return. Consuming persistent authority before M109 is mandatory: Reset during input must discard only local calculation state and must not leave a reusable challenge. The post-input age check is also mandatory so operator delay cannot extend the validity window.
- Reset, alarm, another Offset Loader, missing/invalid inputs, macro-version mismatch, and power-cycle recovery must all fail closed. No protected variable becomes a persistent Server workflow mode.

`scripts/new-haas-verification-commissioning-pack.ps1` now generates a reversible,
local-only commissioning candidate that uses one controlled `M109` prompt per
response digit. It accepts only a configured response variable in Haas's documented
`#500`-`#549` or `#10500`-`#10549` target ranges, clears that variable after every
digit, and marks every output `COMMISSIONING_CANDIDATE_NOT_PRODUCTION_APPROVED`.
This is a prepared test candidate, not a settled production input decision: HFO
review and physical tests must prove cancellation, Reset, timeout, look-ahead,
variable collision, alarm-before-motion, and leading-zero behavior on the exact
control before the generated programs may be approved.

The generator reads the already-derived six-digit Machine key only from a local
JSON file, refuses the public `271828` key unless explicitly producing an isolated
bench pack, and permits key-bearing output below the repository only in the
git-ignored `.diagnostics` tree. It never accepts or derives the Server secret.
The companion `scripts/new-haas-verification-local-config.ps1` performs the
documented derivation through an interactive secure-string prompt, writes only the
derived key to a required `*.local.json` file, and never prints either secret or
key. The local file is still sensitive commissioning material and requires host
access control and deletion/archival under the site's credential procedure.
The generated pack contains the protected challenge and verification programs,
first-executable-block hook, final Offset Loader call, cycle event blocks, and a
matched no-motion test pair. The test Offset Loader performs no offset writes and
calls the challenge only as its final action before `M30`; the test NC begins with
the exact verification hook and emits no production cycle events. Machine-specific
generation requires the current Server-issued six-digit Offset Loader token and
immutable NC identity; the public bench pair works with a Server only when an
isolated development context intentionally contains its published values. Every
artifact is covered by the SHA-256 manifest. The bundled development Machine-output
scenario can emit the matching strict event shapes over loopback TCP or as an
ASCII/CRLF transcript.

## Mandatory bench acceptance

The physical record must show:

- all seven vectors match independently on Server/script and CNC;
- each single-input change produces the published different response;
- `0282` is displayed with its leading zero and numeric entry `282` compares correctly;
- the protected macro rejects undefined, fractional, negative, five-digit, and seven-digit inputs;
- wrong entry clears the response and raises an alarm before any motion;
- correct entry clears the response and emits success exactly once;
- Reset and power cycle do not preserve an unsafe success;
- ordinary operators cannot reveal or alter the protected numeric key;
- the selected input method, variable ranges, look-ahead control, and rounding behavior are recorded for the exact controller version.

Official Haas references: [Mill macros](https://www.haascnc.com/service/online-operator-s-manuals/mill-operator-s-manual/mill---macros.html), [G65](https://www.haascnc.com/service/codes-settings.type%3Dgcode.machine%3Dmill.value%3DG65.html), [M109](https://www.haascnc.com/service/codes-settings.type%3Dmcode.machine%3Dmill.value%3DM109.html), [DPRNT](https://www.haascnc.com/service/troubleshooting-and-how-to/how-to/communication-with-external-devices---dprnt.html), and [Setting 23](https://www.haascnc.com/service/codes-settings.type%3Dsetting.machine%3Dmill.value%3DS23.html).
