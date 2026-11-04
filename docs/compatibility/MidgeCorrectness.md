# Current correctness pass

The supported local correctness checkpoint passed on 2026-11-04. The subsequent
[behavior integration checkpoint](MidgeBehaviorAlignment.md) also passed;
comparative performance qualification now follows in the
[requested execution order](MidgeParityExecutionOrder.md).
The reference is Midge 0.5.2 main `c684310d9d8ce8ecd1151c47151d4bea98b348cf`
and lsm-spec main `2376ba9ddc7a9dc55d5988babc3847089ea8d3e1` (FORMAT 9).
This pass preserves the existing dirty migration and earlier fixes.

## Corrected decisions

| Area | Midge-aligned decision and executable evidence |
| --- | --- |
| Read authority | Cloud reads require current authority on entry and completion, even for cached, missing, own-intent, empty and limit-zero results. A scan fences permanently after an authority failure. Provider and filesystem-simulated takeover cases are covered. Local retained reads stay available after a writer fence; Closing permits retained reads while rejecting new work. |
| Runtime ownership | WAL workers take immutable sequence input and return typed commit receipts. They no longer advance `RuntimeState.Sequence` or publish health. Synchronous flush requests capture only immutable family/origin facts before queuing. `WalRuntimeOwnershipTests` reproduced early worker mutation for both single and grouped writes before the change. The coordinator publishes only a completed visibility cut. |
| Atomic apply | Single and grouped transactions stage immutable family roots, tombstones and checked accounting before publication. Late spill corruption after durable WAL leaves the old visible cut intact, returns an unknown outcome, fences further mutation, and recovers the complete transaction on reopen. This replaces partial live mutation and grouped mutate/undo. |
| Authorized recovery | Durable family frontiers select complete replay units. Unknown family authority fails closed. Sealed members are validated against the ledger before replay; salvage stops at the first damaged authorized member, persists its allocation floor, and excludes every younger member. Active-tail discovery remains read-only until selection is validated. |
| Typed failure boundaries | Unsupported WAL/SST versions, exhausted decoder budgets and provider timeout/busy/I/O failures cannot be converted into salvageable corruption. Batch sequence reservations must not overlap earlier physical history or start before their segment. |
| Publication and cleanup | Publishing a committed version queues obsolete-file cleanup through the existing maintenance owner/GC worker. A later journal failure during cleanup records degraded health without replacing the successful commit reply with an ordinary I/O exception. Claim identities and request generations preserve wake-ups during collection and delayed response handoff. Read-only completion releases pins without performing fenced maintenance. |
| Family drop and pins | Drop publishes the new version through the direct-read admission gate before reclamation. A newly admitted reader cannot retain the old family after GC has already checked its pins; readers pinned before publication keep their sources. The regression reproduced a lazy SST read failing after deletion before this change. |
| Cloud recovery authority | Selected metadata must reach the shared generation named by catalog retirement. Missing, nil or older authority cannot bootstrap an empty database. Catalog recovery probes without repair before metadata admission, then rechecks the retirement cut before staging cache bytes. Equal-epoch pointer claims require both holder and owner token. |
| Backup authority | Capture reads the durable ledger and pins exactly its canonical members. It ignores numeric aliases/orphans and fails on missing or incompatible authority. Simulated-cloud artifacts preserve lease identity, epoch and metadata pointer while expiring only the copied lease, following Midge backup semantics. Native coordination scratch is excluded; legacy objects are fully checksum-validated before restore normalization. Actual restore leaves a live source's authority unchanged. |
| WAL family scope | Resident batch envelopes and spilled begin/commit markers carry the transaction's family. Both encoding and replay enforce Midge's single-family transaction scope. Group admission validates every transaction before reserving ledger sequence space. Eight failing codec/spill cases and a real Midge consumer rejection identified the prior outer-family-zero bug; three grouped-admission regressions proved rejection previously changed durable ledger bytes. |
| Flush success accounting | Failed local immutable publication attempts receive no success count or duration. The coordinator records one matching installation and elapsed time from its first accepted submission through retries. The bounded metadata ledgers and exact-cut cloud publication evidence are described in the accounting note; campaign qualification remains separate. |
| Flush retries and resource owners | Automatic retry timers target their exact retained generation, preserving other families' backoff. Pre-worker rejection settles the attempt; recovered output claims remain with the generation across retries. Recovered-hybrid budget tests qualify real components; current public Actor modes do not reach that combination. |
| Initial provider authority | The first immutable metadata generation is staged in independently accounted MemoryOnly storage, confirmed remotely, and hydrated before local FORMAT/manifest/WAL creation. Provider and simulated opens hold one cache exclusion handle before remote effects and transfer it to storage. Physical identity checks protect replacement writers across awaited startup. See the bootstrap note. |
| Deferred tail validation | Deferred opens leave the original active-WAL durable marker unchanged until replay has validated the tail. The fixed path recovers the original four-cycle crash history with zero ledger-model violations; a default process regression retains that schedule. Actual loss within a synced prefix still fails closed. |
| Manual compaction ownership | One original response deadline spans preflush, prior maintenance, retries and accepted publication. Caller timeout keeps accepted staging charged until its physical completion. Three real-engine regressions cover the prior unbounded waits and prematurely released hybrid claim. |

The detailed source, test and boundary maps are in
[storage](MidgeCorrectnessStorage.md),
[transactions/publication](MidgeCorrectnessTransactions.md),
[resource ownership](MidgeCorrectnessResources.md),
[completed recovery work](MidgeRecoveryProgress.md),
[provider bootstrap](MidgeProviderBootstrap.md),
[unsigned numeric values](MidgeNumericWidths.md),
[manual compaction](MidgeManualCompactionDeadline.md),
[cloud authority](MidgeCorrectnessCloud.md) and
[accounting/progress](MidgeCorrectnessAccounting.md). They record intermediate failed
qualification checkpoints rather than rewriting them as final green evidence.

## Producer/consumer evidence

The [local FORMAT 9 matrix](Format9Interoperability.md) passes both producer
orders across SST and WAL-only handoffs, plus a 100-operation non-default-family
spilled WAL consumed by Midge. These are 23 engine opens on macOS arm64, using
the actual Midge library and matching third-party dependency versions.
`CurrentMidgeFormat9WriterTests` preserves unchanged Midge writer bytes as an
ordinary behavioral regression input.

No format version was bumped in this pass. Previously nonconforming Pants WAL
with an outer/member family mismatch is rejected rather than silently migrated.
Conforming FORMAT 9 data remains the target; older database formats still need
logical export/import into fresh storage.

## Integrated design and remaining gates

The startup, progress and accounting changes have independent ownership and
qualification boundaries:

- Optional aggregate `OpenTimeout` now applies across all storage modes,
  independently of response/storage timeouts. Startup prepares a dormant runtime
  and transfers it through an atomic admission decision; abandoned work retains
  authority through physical cleanup. See [startup ownership](MidgeStartupDeadline.md).
- Completed recovery work now has explicit per-open producer ownership and
  caller/phase consumer scopes. The [progress audit](MidgeRecoveryProgress.md)
  identifies producer coverage and the remaining external-watchdog campaign.
- The engine accounting slice now implements bounded owner/operation ledgers,
  explicit origin/medium, delegated versus durable byte evidence, exact-cut
  publication receipts and interval validity. ACCT-6 measurement integrity is
  integrated and passes 89 combined focused cases; full campaign execution follows
  behavior qualification. The provider bootstrap now uses an independent
  MemoryOnly staging owner.
- Actual current Midge WAL/SST producer images above `Int64.MaxValue` now retain
  unsigned runtime cuts through snapshots, conflicts, flush/compaction and reopen.
  Public sequence diagnostics are `ulong`; epoch-opening and live-allocation
  exhaustion have separate checked rules. The [numeric note](MidgeNumericWidths.md)
  records API compatibility, focused evidence and TTL cross-engine readback.

This is a bounded source-and-regression audit, not a proof of every schedule.
The domain notes identify unreviewed completion reorderings, independent-process
power loss, native provider ambiguity/deletion schedules and some shared
frontier/retention combinations. The writer matrices qualify named local and
simulated-cloud handoffs; every codec/TTL combination and Linux/Windows behavior
remain separate.
Hosted CI and production provider environments are separate evidence.

The historical per-case inventory still needs a current-source review. An old
mapping or aggregate passing-test count is not an independent invariant proof.
Behavior changes must preserve this checkpoint's authority, publication,
recovery and ownership guarantees. Comparative performance qualification follows
the behavior phase.

## Integrated local qualification, 2026-11-04

Locked restore, formatting verification and a forced Release solution build
passed with zero warnings/errors. The complete frozen Release test assembly
passed **3,393** cases with zero failures and two expected skips in 11 minutes
42 seconds. All **15** Sqrzl provider/crash-smoke cases passed. The skipped live
AWS test remains unexecuted; the skipped extended soak was then run explicitly.

The extended campaign passed nine seed/backend runs: seeds 3131, 7717 and 2929
across Local, simulated cloud and Sqrzl, with four cycles and 96 planned
operations per cycle. All 36 cycles passed their recovery-model checks. Some
planned triggers fell back to workload completion; exact actual endpoints are
retained in the TRX output. Both simulated cloud and Sqrzl reached seed 2929's
previously failing `MidWalAppend` hit 25.

Fresh Pants unsigned WAL-only and flushed-SST continuations at sequence
`9223372036857921555` were each consumed by native current Midge, which read all
three exact rows and retained that applied snapshot cut separately from its new
allocation floor.

The retained results are `correctness-final-integration-focused.trx` (40 cases),
`correctness-final-combined.trx` and `correctness-final-extended-crash.trx` under
the ignored test result directory. The source/build/fixture manifest covers
1,403 inputs; 232 DLL/JSON output candidates also remained unchanged through
qualification. Hashes and campaign scope are in
`/tmp/pants-final-correctness-20261104.qualification.json`; final native readback
is in `/tmp/pants-final-u64-interop-20261104/qualification.json`.

This qualifies the named supported local checkpoint on macOS arm64. It does not
claim complete behavior or performance parity, live AWS operation, Linux/Windows
native behavior, hosted CI or every fault schedule.

## Frozen first-batch qualification

Before integrating the later startup, progress and accounting work, locked
restore, formatting verification and a zero-warning Release solution build
passed. The full Release suite against isolated Sqrzl passed **3,238** cases
with **2** explicit opt-in skips (live AWS S3 and extended crash soak), including
all **15** Sqrzl provider/crash-smoke cases. The retained result is
`midge-correctness-batch-qualified.trx`; `/tmp/pants-correctness-batch-qualified-20261104.json`
records its hashes and unchanged source-input proof.

Intermediate full runs exposed immediate-cleanup assertions, an attempt waiter
signaled before test-owner cleanup, and a non-atomic crash readiness marker.
Those test schedules were corrected without weakening the physical deletion,
typed admission or process-crash recovery assertions. This first-batch result
does not qualify the subsequent integrated changes; their gates follow them.
