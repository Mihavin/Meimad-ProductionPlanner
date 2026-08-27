# Meimad CNC event simulator

This is a development-only Haas DPRNT TCP peer. It listens on loopback by default,
waits for the Server's read-only DPRNT client, and emits strict `MEIMAD/V/1` lines
from a JSON scenario. It adds no Server endpoint and cannot mutate a production
Server unless an administrator deliberately configures a development Machine to
connect to it.

Validate the bundled scenario without opening a socket:

```powershell
dotnet run --project tools/Meimad.Planner.CncSimulator -- `
  --scenario tools/Meimad.Planner.CncSimulator/scenario.full.json --validate-only
```

Run the TCP simulator:

```powershell
dotnet run --project tools/Meimad.Planner.CncSimulator -- `
  --scenario tools/Meimad.Planner.CncSimulator/scenario.full.json `
  --bind 127.0.0.1 --port 8080
```

Write the exact ASCII/CRLF Machine-output transcript without opening a socket:

```powershell
dotnet run --project tools/Meimad.Planner.CncSimulator -- `
  --scenario tools/Meimad.Planner.CncSimulator/scenario.verification-commissioning.json `
  --output .diagnostics/verification-machine-output.txt
```

The output command refuses to overwrite an existing transcript unless `--force`
is supplied. The commissioning scenario models a failed attempt, a fresh Offset
Loader challenge, success, one valid cycle, duplicate delivery, and an out-of-order
cycle boundary. It simulates only the Machine's strict output; it does not calculate
or disclose an operator response or a protected Machine key.

The scenario requires `machineId`; configure that development Machine's DPRNT
connection to the simulator. Machine identity is connection-scoped and is not
fabricated inside a DPRNT line. Each event controls evidence through
`productionRunId`, `programIdentity`, `offsetRelease`, `nonce`, `sequence`, and
`macroVersion`. Optional `atMs` schedules the event relative to connection time,
`delayMs` adds network delay, and `repeat` duplicates delivery. DPRNT v1 contains
no Machine-clock timestamp, so the simulator does not pretend it can submit one;
the Server retains actual receipt time. Explicit sequence/timing values allow gaps,
delay, and out-of-order delivery. Another `OFFSET_LOADER_COMPLETED` can identify
the next Production Run.

The scenario contains CNC encodings for QC/tablet event names so the wire parser
can be exercised, but the production Server deliberately defers those CNC events.
Official `SEND_TO_QC` remains tablet-authenticated and QC PASS/FAIL remains an
authorized Windows action. The automated end-to-end test uses those official paths.
