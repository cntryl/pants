# Resource ownership and admission correctness

This bounded review uses Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf`
and RES-1 through RES-8 in its invariant catalog. It reviews the current Pants
resource owners and the specific failure schedules below. It does not qualify
every resource consumer or every Midge regression by association.

The cross-family retry defect below is reproduced through the current public
local engine. Recovered-hybrid admission and reservation tests compose actual
production WAL/store, ledger and pipeline components. That combination is not
currently reached by public Actor open: immutable background flushes are
local-only, while hybrid cache admission belongs to cloud modes. Those component
repairs do not establish a newly reproduced public-open wedge or data loss.

| Invariant | Reviewed production boundary and evidence | Open qualification boundary |
| --- | --- | --- |
| RES-1: bounded resources remain charged with their owners | Live flush headroom transfers from accepted WAL admission into `LocalDiskStore`'s frozen generation claims. Recovered generations now use the same retained owner instead of releasing their claim when one publication attempt fails. `RecoveredFlushAdmissionTests` uses a genuine recovered WAL, actual staging bytes, the real hybrid ledger and a successful later install/reopen. | Exhaustive metadata decode/capture ownership, native callback tails, reader retention and whole-process memory accounting were not independently requalified here. |
| RES-2: admission before effects | Hybrid WAL append reserves WAL/output capacity before append; prior accepted intents and recovery authority remain intact on rejection. `PantsHybridStorageTests` exercises actual WAL length/sequence preservation and reopen after single/group admission rejection. | Every publication and provider request admission site has not been exhaustively reviewed. |
| RES-3: typed saturation preserves accepted state | Storage budget admission reports `PantsNoSpaceException`; bounded runtime channels wait for capacity and settle rejected admission without losing counts. Recovered pre-worker flush rejection now reaches the owned attempt completion path, retaining its generation and resolving the typed failure waiter. | Arbitrary allocator failures and every worker error mapping are outside this bounded review. |
| RES-4: bounded retries preserve the exact generation | `ImmutableFlushPipeline` uses capped exponential delays; the actor now schedules only the validated generation whose retry timer fired. `PantsImmutableRetryOwnershipTests` uses actual local WAL/flush work with virtual time to prove that a younger due family cannot steal an older family's backoff and both families subsequently publish. | No CPU-utilization or sustained fairness SLO follows from these bounded schedules. Permanent provider failure may retain data and prevent progress. |
| RES-5: caller timeout preserves accepted ownership | Runtime response timeout/cancellation abandons the response while admitted work remains runtime-owned. WAL requests retain scalar sequence inputs and return receipts. `PantsRuntimeResponseTimeoutTests`, ownership and post-WAL apply tests cover acceptance/unknown outcome and later recovery. | The remaining deadline supplied to every native provider operation, startup phase and maintenance retry was not exhaustively requalified in this resource review. |
| RES-6: published and pending L0 generations consume slots | `MemtableWritePressure` counts published L0, queued/in-flight immutable generations and a nonempty active generation. Recovered L0 counts and the admission watchlist are initialized before startup exposes the runtime. A below-threshold active generation may fill its reserved last slot; reaching the threshold stalls the next write even when automatic freezing failed. Pressure recovery includes the active reservation with ordinary background compaction disabled. `PantsL0AdmissionTests` covers bounded public L0 and over-ceiling reopen; `PantsL0FailedFreezeAdmissionTests` holds real compaction compute and proves pre-WAL refusal with unchanged sequence/LEDGER/WAL bytes, exact rows and reopen, plus below-threshold and successful-freeze controls. | These four failed-freeze/blocked-worker schedules do not qualify arbitrary backlog geometries, every native allocation failure or all provider admission schedules. |
| RES-7: bounded compaction work and scratch | Compaction reservations, bounded merge heads and repair scratch retain authoritative inputs on refusal. Scratch residue prevents fresh admission until cleanup is verified. Repair scratch and retention regressions exercise these owners. | A global decreasing progress measure, arbitrary overlap geometries, sustained service rates and device write amplification are not established by bounded scratch tests. |
| RES-8: teardown retains writer authority through physical work | `Actor.DisposeAsync` joins channel services before releasing storage; a timed-out `RuntimeWorker` contributes its actual `Completion` to the ownership reaper. Cloud lease heartbeat, lease, cache and local store are released after worker exit. `PantsRuntimeLifecycleAdversarialTests` and blocked-flush tests exercise same-path lease exclusion and later recovery. | This is source/test evidence for those finite held-worker schedules, not termination under a permanently hung backend or every cloud failed-compute cleanup schedule. |

## Concrete repairs

Midge routes flush identity/admission/submission failures through
`fail_flush_pipeline`, retaining the immutable and settling its waiters. Pants
previously called `BeginAttempt` before requesting recovered output capacity,
then escaped on rejection with no worker or completion observer. A real
recovered-WAL/hybrid-ledger regression first failed with `NoSpace`; the attempt
now settles through the ordinary owned completion and retry path. The observer
is detached because the caller can be the actor's current turn: awaiting an
owner command there would deadlock the runtime.

Midge's built flush owner retains its output reservation across publication
failure (`flush_pipeline/settlement.rs::fail_flush_pipeline`). Recovered Pants
flushes previously scoped that reservation to one physical attempt while their
staged output survived for retry. Recovered claims now enter the same frozen
generation owner used by live accepted writes. Retries reuse that claim;
successful storage completion or owner teardown releases it. Already-completed
generations retry validation/intent cleanup without a new output claim.

Midge selects only due retained family heads
(`state/flush.rs::begin_next_immutable_flush_matching`). Pants previously let
any generation's timer invoke the global retry selector. The real-engine
virtual-time regression observed family 0 publish at family 1's earlier retry
deadline. The timer now schedules its exact validated retained generation;
explicit manual retry keeps its existing policy override.

At the final L0 slot Pants previously exempted every nonempty active memtable
from admission refusal, while Midge exempts only a below-threshold generation
(`state/flush.rs::l0_write_slot_unavailable`). A real local-engine regression
publishes twelve L0 SSTs with a thirteen-slot ceiling, holds actual compaction
compute, and injects a maintenance failure before automatic freezing after a
threshold-crossing Sync commit. The next write was wrongly accepted. Admission
now tests the resolved flush threshold before WAL allocation. The regression
checks unchanged runtime sequence and next-WAL sequence, counters and complete
WAL-directory file bytes including LEDGER; live reads and reopen retain exactly
the acknowledged rows. A below-threshold generation remains writable, and a
successfully frozen last slot still stalls.

The same accepted active reservation was omitted from pressure-recovery
scheduling. Without a manual request, the real engine never started compaction
after the injected pre-freeze failure. Recovery selection now uses complete
slot usage for live families, including their active generation, matching
Midge's distinct urgency rule. The fourth regression holds then releases that
automatically scheduled compute and proves publication drains the pressure
with ordinary background compaction disabled. The injected failure is at the
existing pre-freeze maintenance failpoint; it does not qualify arbitrary
failures inside native allocation or every internal detach boundary.

## Validation boundary

Locked restore and isolated Release artifacts use
`/tmp/pants-correctness-tx-20261104`. The initial recovered admission regression
failed before the fix. The first recovered admission/staged retry, pipeline and
hybrid-storage filter passed **40/40** cases. The real-engine cross-family retry
regression then failed with the wrong family published at the younger deadline.
The completed-generation cleanup edge also failed with a needless output
admission rejection before the final pending-owner predicate was added. The
final resource/completion filter passed **48/48** Release cases with no build
warnings or errors: recovered component admission/retention/cleanup, real-engine
retry ownership, pipeline/hybrid storage, L0 admission and apply-fence completion.
Repository-wide qualification is a separate gate.

The later RES-6 follow-up uses an isolated snapshot at `/tmp/pants-res6-review`
with artifacts in `/tmp/pants-res6-review-artifacts`. Its four real-engine
schedules first produced two failures (the next write was accepted after the
threshold-crossing failed freeze, and automatic pressure recovery never
started) while both controls passed. After the predicate repairs, all four
passed; the broader L0, immutable flush pipeline, retry ownership and recovered
admission filter passed **69/69** Release cases with no build warnings or errors.
No full-suite or provider schedule qualification is inferred from this filter.
