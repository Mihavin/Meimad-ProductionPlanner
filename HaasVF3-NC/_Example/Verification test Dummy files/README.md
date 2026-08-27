# TestServerVerification005 - Operation 10 - Server verification test

These files are commissioning-only, no-motion inputs for the Planner Server
verification workflow. They do not make macro v5 production-approved.

- `O01992-SERVER-VERIFICATION-EVENT-TEST.CNC` has NC identity `328934`, which is
  already immutably bound to the accidental Doosan release and must not be
  published again.
- `O01994-HAAS4X-SERVER-VERIFICATION-EVENT-TEST.CNC` has the new NC identity
  `817426`. Its first executable block is exactly one `G65 P9002 A817426.` hook.
  After successful verification it stops at `M00` so `SEND_TO_QC` and `QC PASS`
  can be completed before it emits strict `CST` and `CEN` DPRNT events.
- `O01993-SERVER-OFFSET-LOADER-TEMPLATE.CNC.txt` performs no offset work. It is
  intentionally not loadable until the Server creates the current Offset Loader
  release and its `SERVEROFFSETTOKEN` placeholder is replaced exactly once.
- `TOOL-TABLE-NO-TOOLS.csv` is a structured, header-only tool table. The Server
  parses it as zero active and zero required tools.

Required publication order:

1. Publish O01994 for the `HAAS_4X` postprocessor as a new immutable release for
   the current Operation 10 process revision, confirming the dummy tool table.
2. Explicitly select that exact G-code release on the dedicated test Production
   Run assigned to VF-3SS.
3. Create a current Offset Loader release using the exact NC and dummy tool-table
   release IDs.
4. Replace `SERVEROFFSETTOKEN` with the returned six-digit token and save the
   final loadable file as `O01993-SERVER-OFFSET-LOADER-TEST.CNC`.
5. Verify hashes and run the bounded preflight before transferring either file.

Do not reuse the earlier identity `654321` or token `483920`; they belong to the
earlier immutable bench release and cannot identify this new Server release.
