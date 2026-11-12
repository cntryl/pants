# Transaction completion and cloud confirmation

This bounded slice follows Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf`.
It changes public transaction behavior and adds interface members; consumers
implementing these interfaces must implement the new members. It does not
change the pinned FORMAT, WAL, SST, manifest or lease encoding.

| Contract | Midge source | Pants behavior and executable evidence |
| --- | --- | --- |
| TTL starts at successful serialized preparation | `src/runtime/actors/wal/transaction.rs::build_transaction_wal_batch`; `src/runtime/actors/wal/spill.rs`; `src/runtime/state.rs::observed_time_millis` | `CommitPayload.PrepareOperations` creates one immutable observation after validation and write admission. Both WAL and live apply traverse that same prepared source. `PantsTransactionBehaviorPreparationTests` holds an earlier physical append, proves actual later enqueue, advances the injected TTL clock, and checks exact resident/spilled values and inclusive expiration after reopen. A separate held-own-append case proves completion does not resample expiration. |
| A range has strictly increasing binary endpoints | `src/engine/api/transaction.rs::delete_range` | Equal endpoints throw `PantsInvalidArgumentException` immediately, preserving prior intents. Memory, local and simulated-cloud cases prove no sequence/WAL allocation for a rejected-only range and recover the earlier valid intent. This is an API/resource behavior correction; Pants previously encoded and reopened its accepted empty range, so the regression does not claim data loss. |
| Backend-incompatible durability is a caller error | `src/engine/api/transaction.rs::effective_wal_durability_policy`; `tests/transactions.rs` | Empty and writing transactions reject unsupported backend policies with `InvalidArgument` before effects. Read-only completion still ignores the write durability policy. |
| Completion returns the transaction's actual cut | `src/engine/api/transaction.rs::commit_with_sequence`; `src/runtime/actors/wal/transaction.rs` | `CommitWithSequenceAsync` carries an immutable `CommitCompletion` receipt through the existing response registry. Writes return their individual assigned sequence, including coalesced writes; read-only/pure-empty completion returns its start cut; assertion-only completion returns the validated current server cut without allocating a sequence. Concurrency, assertion, spill and reopen tests check these distinctions. |
| Pure empty CloudStrict is a no-op | `src/engine/api/transaction.rs::commit_with_sequence`, no-write/no-assert branch | Policy validation remains, but unrelated pending uploads are not confirmed. Assertion-only CloudStrict still validates and confirms the current server cut. Local empty Sync retains the explicit physical sync boundary. Apply-fenced completion tests preserve cleanup-only completion and reject actual guarded effects. |
| An accepted unknown outcome requires reconciliation | `src/common/error.rs::Severity::OutcomeUnknown` | `CommitOutcomeUnknown` maps to the distinct `PantsErrorSeverity.OutcomeUnknown`, rather than `Fatal`. The error code, runtime ownership, write fence and recovery behavior remain intact. An actual accepted append held beyond its response deadline returns this severity, then recovers its exact value. |
| Cloud durability is an explicit target confirmation | `src/engine/mod.rs::cloud_durable_sequence`, `wait_cloud_durable`; `src/runtime/event_loop/wal.rs::seal_for_cloud`; `tests/cloud_core/cloud_durable_wait.rs` | `GetDurableSequenceAsync` reads the confirmed cut. `WaitForDurableAsync` guards lease authority before covered-cut success, can seal a covering WAL early, and observes existing callerless publication retries. It returns false for the original caller/runtime confirmation deadline or a transient/backpressure result. Accepted physical work survives timeout/cancellation. |

The cloud wait uses one caller observation budget capped by
`RuntimeResponseTimeout`, including coordinator queue time. The coordinator
admits each observer and routes one typed outcome. Acknowledgement, error,
accepted shutdown, cancellation and deadline expiry claim that observer once;
later events cannot overwrite its outcome even when its caller continuation is
delayed. The existing bounded submission queue does not impose an additional
limit on live observers of the same upload.

Target eligibility uses an actual covering WAL segment. An unrelated upload
does not make an impossible future target eligible; no covering work retains
Midge's `Internal` result. Accepted shutdown ends pending observations with a
retryable result before joining physical workers. A refused shutdown with live
snapshots does not claim them. Lease checks still reject authority loss while
an observation remains pending, and caller cancellation preserves its token
identity. A false result is not an authoritative failed write and does not
revoke publication ownership.

Recovery restores that cut from the validated committed catalog anchor and
verified published WAL prefix, following Midge's
`engine/startup/recovery.rs::reset_cloud_durable_sequence_for_recovery`.
Retirement already binds the anchor to an admitted committed metadata
generation. The cut does not come from the allocator floor, local manifest or
current visible sequence. `PantsCloudDurableRecoveryCutTests` reproduces both
provider and simulated-cloud cut regression after strict commit, flush and WAL
retirement, and verifies the restored exact value and zero-budget confirmation
after reopen. The soak migration originally caught this missing recovery cut.

The frontier also follows Midge `runtime/frontiers.rs::advance_cloud_over`
(#843): after the full predecessor is acknowledged, it advances across the
unused sequence range below the actual open WAL's first sequence. A store-owned
gate captures those two immutable ledger facts; startup and the serialized
cloud acknowledgement lane apply the rule. Neither the reservation counter nor
a diagnostic current sequence proves coverage. Provider and simulated recovery
tests request a persisted reserved hole after an empty restart. A held earlier
acknowledgement followed by a newer sealed, unacknowledged WAL proves that the
rule cannot skip an outstanding predecessor.

`PantsCloudDurabilityWaitTests` uses the production Azure protocol adapter with
an in-memory HTTP backend. Held WAL PUTs prove caller and runtime deadlines,
queue expiry without a later seal, cancellation, and later successful retry.
Real monotonic lease expiry rejects both an uncovered in-flight wait and an
already covered cut. Provider images copied before graceful shutdown or a
held effect completes recover a confirmed sentinel and only the confirmed
second write in a fresh cache, under an expired exact copied lease. These are
controlled backend-image schedules, not native provider or power-loss proof.

The crash/soak verifier now obtains its read-verified cut from a read-only
`CommitWithSequenceAsync` and calls `WaitForDurableAsync` explicitly. The hybrid
cache readback-retry test uses explicit maintenance instead of relying on an
empty transaction to run housekeeping.

Validation artifacts and exact commands are exported with the frozen patch.
Focused suites cover the changed transactions, TTL, spills, coalescing,
durability, read authority, lifecycle, provider confirmation and crash/reopen
paths. Full latest-tree qualification belongs to the integration gate; these
bounded runs do not establish every cancellation interleaving or native
provider/platform schedule.
