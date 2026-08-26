# Haas setup-verification response algorithm

## Status

Algorithm v1 is an implemented **reference and bench-test candidate**, not commissioned production behavior. It is not connected to verification sessions, tablet responses, workflow transitions, or CNC enablement. The actual Haas control must reproduce every published vector, including leading-zero responses, before Milestone D may use it.

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

`V01` through `V07` correspond to the table order. The line contains no Machine key. `scripts/haas-verification-spike.ps1` remains read-only/passive and now grades these captured lines into `responseVectorGrade`; missing, duplicate, malformed, unexpected, or mismatched attempted vectors make the status `FAIL`. A capture containing only the separate `MEIMADSPIKE/CASE/...` identity probes reports vector status `NOT_RUN`, not a vector failure. Captured `MEIMADSPIKE` lines are evidence only and are never ingested as operational workflow events. An existing capture or exported line file can be graded independently with `scripts/haas-verification-grade.ps1`.

## Protected-program layout candidate

No `.NC` macro is shipped yet because program numbers, variables, DPRNT behavior, `FIX` arithmetic, Reset cleanup, and access protection still require HFO approval on the actual control.

- The Offset Loader calls the configured protected challenge program only after every offset write succeeds. That program first invalidates prior success, establishes a fresh six-digit nonce, retains the current six-digit Offset Loader token, and emits the strict `OLC` DPRINT.
- The approved NC file calls the configured protected verification program as its first executable block and passes its six-digit identity through `A...`; Haas documents that `A` maps to local variable `#1`. The decimal point is mandatory in the Meimad hook so the six-digit value is not scaled as an integer macro argument.
- If the same nonce, Offset Loader token, and NC identity are already verified, the macro may return. Otherwise it keeps verification invalid, obtains the operator's tablet-displayed response through the commissioned input method, calculates v1 independently, clears the entered response, and either raises the commissioned `#3000` failure alarm or emits `SVS` and permits return.
- Reset, alarm, another Offset Loader, missing/invalid inputs, macro-version mismatch, and power-cycle recovery must all fail closed. No protected variable becomes a persistent Server workflow mode.

The initial operator-entry candidate is the configured response variable plus a protected programmable stop/message. The commissioning test must decide whether that is acceptable or whether six controlled `M109` digit prompts are required; current Haas documentation describes `M109` as single-character input and restricts its target variable range, so this choice must not be guessed in deployable macro code.

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
