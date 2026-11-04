# Manual compaction deadline ownership

This slice follows RES-1 and RES-5 at Midge
`c684310d9d8ce8ecd1151c47151d4bea98b348cf`. Its named reference decisions are
`runtime/event_loop/compaction/deadline_tests.rs`,
`runtime/tests/compaction_deadline_tests.rs` and the manual publication-budget
tests under `runtime/actors/compaction/`. They retain the caller's original
allowance through queueing, prior maintenance, compute and publication; a later
phase or waiter does not manufacture another allowance.

Pants previously constructed its manual deadline only after flushing active
memtables and waiting for earlier compaction. Both waits could therefore outlive
`RuntimeResponseTimeout`. Retrying a stale plan constructed another full budget.
The public coordinator entry now captures one `OperationDeadline`; preflush,
idle waits, scheduling, physical completion and final cloud mirroring consume
that same deadline. The stale-plan loop reuses it. Existing inner actor commands
keep their original-budget cancellation and disabled redundant response timer.

An expired pre-compaction wait returns `PantsTimeoutException`. A deadline spent
after accepting physical mutation keeps the existing unknown-outcome
`PantsIOException` boundary. Neither response authorizes releasing resources
still owned by accepted work.

The prior manual caller's `finally` also released hybrid compaction staging
while its worker could remain blocked. A matching scheduled completion now owns
that lease and releases it only after actual worker completion, including
failure cleanup. Rejected scheduling still releases its untransferred claim.
Background compaction retains its existing independently owned observer.

## Behavioral evidence

`PantsManualCompactionDeadlineTests` first failed both real local-engine cases:
a public manual request stayed pending after its controlled deadline while
earlier background output or its required immutable flush remained held. The
corrected requests return typed timeout before barrier release; same-path opens
remain excluded, live rows remain exact, and all acknowledged rows recover.

The simulated-cloud staging case independently failed with its actual reserved
window falling from 524,288 bytes to zero while physical output remained held.
It now retains that exact reservation through the unknown response, releases it
after physical completion, successfully retries compaction and recovers the
exact rows after reopen. All three focused Release cases pass.

Qualification uses an isolated current-canonical snapshot based on Pants
`fc3baa381dc555b60ffad96a7cc87c6320f72419`, with original source hashes retained
in `/tmp/pants-compaction-deadline-results-20261104/baseline.json`. These are
deterministic actual-engine schedules over local files and the filesystem-backed
simulated provider. They do not qualify native-provider transport behavior,
independent-process power loss or a hardware latency bound. The exact stale-plan
retry transition was source-reviewed for original-budget reuse, rather than
independently fault-scheduled in this slice. Full integrated and platform gates
remain separate from its focused results.
