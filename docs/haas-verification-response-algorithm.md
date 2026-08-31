# Haas setup-verification response algorithm

## Current contract

The response is a short consistency code for the exact setup-verification binding. It is not a Machine identity credential and uses no Machine Secret, derived key, HMAC, token, password, or certificate.

Inputs are the six-digit challenge nonce, six-digit current Offset Loader release token, six-digit immutable NC identity, and configured response width (4–6 digits). The Server and protected V10 macros fold those public binding values with algorithm version 1 and finalization constant `314159`, then retain the requested low-order digits with leading zeroes.

Changing the nonce, Offset Loader release, or NC identity changes the response. Machine recognition remains separately configured by `MachineID`, fixed IP address, and controller MAC address. Recognition does not establish verification success.

## Lifecycle

1. A valid exact `OLC` observation creates `ARMED`. There is no ARMED timeout.
2. The assigned tablet may display the response while ARMED.
3. The first intended NC start emits exact `SVR` evidence. The Server changes ARMED to PENDING and starts the configured timeout at Server receipt.
4. A matching in-time `SVS` changes PENDING to SUCCEEDED. A matching failure or timeout fails closed.
5. Later starts for the same successful Run/Machine/NC/Offset Loader binding do not require another operator prompt.
6. A new Offset Loader release supersedes the previous armed, pending, or successful binding.

The Server is authoritative. Temporary CNC variables only transport/cache handshake values. The event sequence field is diagnostic evidence: resets, gaps, duplicates, wraps, or manual edits are recorded but never decide identity or verification by themselves.

## Reference vectors

| Nonce | Offset release | NC identity | Digits | Response |
| ---: | ---: | ---: | ---: | ---: |
| 731841 | 483920 | 654321 | 6 | 736536 |
| 731842 | 483920 | 654321 | 6 | 841432 |
| 100000 | 100000 | 100000 | 4 | 1795 |
| 999999 | 999999 | 999999 | 5 | 74795 |

Physical alarm, M109 input, Reset/E-stop, and no-motion behavior still require bounded commissioning for each controller/macro release before production use.
