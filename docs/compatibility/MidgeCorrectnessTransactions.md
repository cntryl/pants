# Transaction, publication and snapshot correctness

This bounded review compares the current Pants worktree with Midge
`c684310d9d8ce8ecd1151c47151d4bea98b348cf` and its invariant catalog. It covers
AUTH-3, TX-1 through TX-8, LIFE-1 and LIFE-2. Catalog references identify the
questions to review; conclusions below come from implementation and executable
behavior. This is not a claim that every Midge transaction scenario has been
ported or that provider/crash qualification is complete.

## Invariant evidence

| Invariant | Midge authority | Pants implementation and behavioral evidence | Review boundary |
| --- | --- | --- | --- |
| AUTH-3: serialized authority and publication | `src/runtime/event_loop/wal_pipeline/tests.rs::should_keep_wal_runtime_state_unchanged_until_owner_drains_completion`; `src/runtime/event_loop/memtable_apply.rs` | WAL requests carry an immutable published sequence rather than `RuntimeState`. `WalCommitResult` and `WalCommitGroupResult` return the committed sequence. The actor consumes receipts; `StagedMemtableApply` builds immutable replacements and publishes the complete roots, accounting and sequence together. `WalRuntimeOwnershipTests` exercises actual single/group WAL work held before flush, including completion without owner publication. | This verifies the WAL-to-memtable path. It does not certify every manifest, cloud maintenance or flush-worker publication boundary. Physical I/O counters remain distinct from runtime visibility. |
| TX-1: one family, frozen horizon and retained sources | `src/engine/mod.rs::begin_tx`; `src/runtime/snapshot_pins.rs`; `src/runtime/handle.rs::read_with_authority` | `TransactionInstance` captures its family, immutable `DatabaseVersion` and snapshot time. Actor begin and scan registration pin that version; direct read-only acquisition and publication share `_directReadAdmissionGate`. `MidgeTransactionIsolationParityTests`, `MidgeTransactionSnapshotTrackingParityTests`, `PantsPointReadDiskResidentTests` and `MidgeSnapshotSpillPressureParityTests` exercise retained reads. Cloud reads now check live authority before and after point I/O and scan advances; `CloudReadAuthorityTests` includes actual held provider reads crossing lease expiry. | Local retention is conservative: any active pin can defer collection of obsolete SSTs. Exact per-file retention efficiency and exhaustive acquisition/collection interleavings are not established here. Cloud pins do not protect against a successor; predecessor reads fence after authority loss. |
| TX-2: newest eligible value and intent order | `src/runtime/read_snapshot/snapshot.rs`; `src/engine/api/transaction.rs::get_value` | `SnapshotReadPath`, `TransactionInstance.ReadVisibleValueAsync`, `TransactionScanEnumerator` and `TransactionIntentLookup` resolve committed and own-intent values. `MidgeTransactionStagingParityTests`, `MidgeTransactionSemanticsHardeningParityTests`, `PantsTransactionSpillBehaviorTests` and `TransactionOperationSourceTests` cover put/delete/put and point/range intent order, including spill. | Existing cases are representative; a new independent exhaustive oracle over arbitrary mixed memtable/SST/spill histories was not built. |
| TX-3: deletion, expiration and binary range boundaries | `src/runtime/read_snapshot/snapshot.rs`; `src/compaction/ttl_gc_tests.rs` | `ScanBounds` uses binary start-inclusive/end-exclusive intersections; snapshot time controls TTL masking. Staged apply retains raw expiration milliseconds. `PantsTtlBehaviorTests`, `PantsRangeDeleteConflictRetentionTests`, `MidgeTransactionSemanticsHardeningParityTests` and retained-SST snapshot tests cover the observable combinations named by those tests. | No exhaustive cross-product of TTL, every storage tier, every tombstone overlap and recovery policy is inferred. SST codec/recovery qualification is recorded separately. |
| TX-4: atomic transaction visibility and recovery | `src/runtime/event_loop/memtable_apply.rs`; WAL recovery transaction state machine | Single and coalesced writes build replacement immutable roots before publishing them. A failure after WAL admission reports unknown outcome and fences subsequent mutation/publication until reopen. `PantsPostWalApplyFailureTests` corrupts an actual later spill frame after durable WAL publication, verifies the old cut and rejected later write, then reopens and verifies every committed row. `PantsCommitCoalescingTests`, crash recovery suites and `PantsDurabilityParityTests` cover group publication and recovery. | The new spill regression is an in-process deterministic fault schedule followed by reopen. It is not independent-process power-loss proof or evidence for every allocator/runtime failure. |
| TX-5: strict write conflicts and assertions before WAL | `src/runtime/event_loop/validation`; `src/engine/api/transaction.rs` | `CommitValidator` checks frozen assertion values and later point/range mutations before append. It reads current durable mutation evidence after memtable release. Coalescing refuses to group dependencies that need earlier writes applied first. `PantsTransactionConcurrencyTests`, `PantsRangeDeleteConflictRetentionTests`, `MidgeTransactionConflictParityTests` and `MidgeTransactionAssertionParityTests` cover these contracts, including flushed/compacted range deletions and spilled transactions. | Pants currently serializes validation with the actor. Midge's detached validation retry machinery is not claimed to be implemented; no concurrent mutation can be admitted during the awaited validation turn. |
| TX-6: rollback and disposal do not publish intents | `src/engine/api/transaction.rs::drop`; transaction write-set ownership | `TransactionInstance.RollbackAsync` unregisters the transaction; `Finish` releases resident accounting and owned spill runs. After admitted unknown commit outcome, the runtime owns the payload and cleanup until its completion. `MidgeTransactionParityTests`, `PantsTransactionSpillTests`, `PantsTransactionSpillCrashRecoveryTests` and `PantsRuntimeResponseTimeoutTests` exercise these ownership boundaries. | No guarantee of successful physical artifact deletion after arbitrary filesystem failure is inferred. |
| TX-7: bounded ordered scans and sticky failures | `src/engine/api/query.rs`; `src/engine/api/iterator.rs`; transaction scan | `ScanBounds` intersects range/prefix bounds and handles binary prefix successors. `ScanInstance` preserves its first terminal failure and checks authority around a step, including normal exhaustion. `PantsScanFailureTests`, `PantsStreamingScanHardeningTests`, `PantsTransactionSpillCorruptionScanTests` and `MidgeTransactionSemanticsHardeningParityTests` cover forward/reverse limits, late corruption and failed iterator state. | Provider success/error races crossing lease expiry have dedicated tests; arbitrary disposal-failure combinations and every cancellation interleaving are not qualified. |
| TX-8: bounded isolation contract | Public transaction API and documented conflict policies | Transactions expose one `IPantsColumnFamily`; ordinary reads are snapshot reads and do not become predicate locks. `PantsTransactionConcurrencyTests.ShouldNotImplicitlyProtectPointReadGivenAbortOnWriteConflict` and `MidgeTransactionIsolationLwwParityTests` preserve last-writer-wins and write-set conflict behavior. | Serializable isolation and cross-family atomic transactions are not promised. |
| LIFE-1: serialized explicit family drop | `src/runtime/event_loop/manifest.rs`; `src/runtime/ddl.rs` | Actor drop is ordered with commits and pending local flushes. Safe drop rejects unflushed committed data; destructive drop is explicit. Local uncertain DDL journal writes fence further mutation; cloud DDL uses durable prepare/registry authority. `PantsLocalDdlAuthorityTests`, `CloudDdlTwoPhaseTests`, `PantsColumnFamilyParityTests` and `PantsCloudDropPressureTests` cover committed drop, ambiguity/reopen and pressure release. | Native provider DDL qualification and every process-crash cut remain separate gates. |
| LIFE-2: dropped-family snapshot retention | `src/runtime/snapshot_pins.rs`; manifest drop/reclaim transitions | A transaction's immutable version retains dropped-family roots and SST identities. Collection defers while snapshots remain, then follows durable manifest authority. `PantsColumnFamilyReclamationTests` covers local and simulated-cloud old snapshots, release ordering and deferred reclamation. | Successor cloud collection is allowed; the old holder must fence reads. Cross-process reader-liveness retention is not provided. |

## Changes proven by this review

The cloud read regressions initially returned old values after actual lease
expiry/takeover. The read guard now follows Midge's `read_with_authority`
pattern: check before the operation and check again after it, replacing stale
results, normal exhaustion and stale I/O errors with a sticky fencing error.
New transaction admission requires authority. Existing pinned local reads
remain usable while shutdown is Closing; new work stays rejected.
Simulated cloud uses its own shared catalog/lease authority as well as its
local lease, rather than relying on the provider-only lease coordinator.
`SimulatedCloudReadAuthorityTests` first reproduced a retained value after a
conditional shared-lease takeover and now covers cached, absent, own-intent,
forward/reverse/zero-limit and new-transaction paths.

The WAL ownership regression initially observed runtime sequence 3 or 6 while
the actual writer was held before flush. WAL workers now receive immutable
inputs and return typed receipts. Physical completion itself does not mutate
the runtime sequence, health or memtable accounting. A test-only owner adapter
lets direct storage tests consume the same receipts synchronously; production
workers do not use that adapter.

The single-commit apply regression initially exposed `row:000` from a failed
transaction after a later successful commit published its partially modified
runtime root. The regression corrupts spill frame 3 at the explicit
`BeforeCommitApply` fault boundary after a complete durable WAL transaction.
Both single and grouped apply now stage immutable roots and accounting, then
publish once. A late source failure leaves the old cut intact, reports
`CommitOutcomeUnknown`, fences later writes and metadata changes, and leaves
restart recovery to apply the complete WAL unit. Shutdown avoids publishing
an uncompleted live cut.

Read-only completion and read-write completion without writes, assertions or
durability effects release their registrations without entering mutation or
publication. Rollback and disposal also release pins before scheduling
housekeeping. Cleanup cannot replace a successful completion reply, and after
an apply failure it retains disk sources until reopen. The local and simulated
cloud cases in `PantsFencedCommitCompletionTests` verify old-cut reads,
read-only/no-op completion, rollback/disposal, rejected durability/flush work,
unchanged WAL counters, and complete WAL recovery without discarded intents.

**Empty CloudStrict behavior:** the subsequent
[transaction behavior slice](MidgeTransactionBehavior.md) aligns the pure
no-write/no-assert completion with Midge: validate the durability policy and
return the start cut without a pending-publication barrier. Explicit target
confirmation uses `WaitForDurableAsync`; the crash/soak verifier confirms its
read-verified cut with that API. Assertion-only CloudStrict still validates
against the server cut and requests confirmation. Local empty Sync retains its
physical durability boundary and must hold writer authority.

The storage sequence reservation floor can exceed the visible recovered cut.
Coalesced preflight uses that allocation floor without exposing it as the
visible runtime sequence; receipts and published memtables identify the
completed commit cut.

## Validation boundary

Validation uses locked restore and isolated Release artifacts under
`/tmp/pants-correctness-tx-20261104`. The bounded transaction correctness
filter passed **410/410** Release cases with no build warnings or errors;
this preceded the final simulated-cloud authority completion case.
The follow-up simulated/provider read-authority, Closing lifecycle, retained
snapshot-pressure and DDL filter passed **27/27** cases after that completion.
The final apply-fence completion, rollback/disposal, dropped-family reclamation
and lifecycle filter passed **17/17** cases. The no-op completion boundary first
failed all three targeted cases before the fix (local, simulated cloud and
coalesced apply failure).
Repository-wide formatting/build/test qualification, native
provider suites and hosted CI are separate evidence and must not be inferred
from this document.

Remaining unreviewed areas include all arbitrary worker completion reorderings,
every metadata authority boundary, independent-process power-loss schedules,
and the complete Midge per-test inventory. The source-and-test review above
does not close those areas by association.
