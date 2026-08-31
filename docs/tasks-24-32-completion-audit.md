# CNC verification implementation audit

This audit supersedes the pre-v63 key/counter design records.

Current implementation facts:

- Machine identity is the configured Planner `MachineID`, fixed controller IP, and controller MAC. No Machine credential is present.
- NC release verification and Offset Loader authorization remain separate, exact, and fail-closed.
- Setup verification uses `OFFSET_LOADER_COMPLETED -> ARMED -> PENDING -> SUCCEEDED`.
- ARMED has no timeout. `SVR`, emitted at the first intended NC start, begins PENDING and its timeout.
- Success remains valid for subsequent starts of the same exact Run/Machine/NC/Offset Loader binding. A new Offset Loader supersedes it.
- Sequence is retained as duplicate/gap/reset/wrap/out-of-order evidence only and cannot block identity, verification, workflow, or an exact cycle pair.
- Temporary CNC variables are transport/cache values and never Server authority.
- Schema v63 removes the former credential column, adds ARMED/pending-start state, and adds the controller MAC configuration field.
- The V10 no-motion generator and structural test are implemented. Physical commissioning remains required before production enablement.
