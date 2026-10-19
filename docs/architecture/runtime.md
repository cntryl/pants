# Runtime Architecture

`Actor` is the single state-transition coordinator. It owns the current
immutable `DatabaseSnapshot` publication and admits commands through a bounded
channel. Reads use frozen snapshots; mutations are serialized by the
coordinator.

`RuntimeBootstrapper` is the asynchronous composition root. It resolves immutable
`PantsOpenOptions` into a Core-owned `RuntimePlan`, opens provider and local resources without
sync-over-async bridges, and constructs the coordinator. `RuntimeComposition` owns the resulting
runtime lifetime. Failed startup and normal shutdown both release leases, stores, workers, and
provider object-store clients in bounded order.

Every command that expects a synchronous runtime response receives a monotonic request ID at
admission and shares one `RuntimeResponseTimeout` budget for its response and nested cloud work.
Queue time and every provider, lease, catalog, manifest, and publication call consume that same
absolute monotonic budget; an expired budget is rejected before another provider request can be
submitted. Individual provider calls remain capped by `StorageTimeout`, so neither nesting nor
retry resets the aggregate deadline. If the caller's own
cancellation expires first it remains authoritative. If the runtime-response budget expires,
Pants removes the live waiter, retains only bounded and expiring request-kind/timing metadata, and
reports `PantsTimeoutException` with an outcome-unknown diagnostic. The accepted command is not
cancelled: commits, provider publication, fencing, and cleanup remain runtime-owned. A later
terminal response is consumed once and counted by `RuntimeLateResponsesTotal`; current waiters,
bounded tombstones, and total abandonments are available in `PantsRuntimeMetrics`.
An admitted `CommitAsync` whose response deadline expires reports
`PantsCommitOutcomeUnknownException`; cancellation after commit admission reports the same type.
The transaction wrapper does not enqueue a rollback after either response, because the accepted
commit remains runtime-owned.

Open/recovery is not an admitted runtime-response wait: provider/storage calls made while opening
share one startup `RuntimeResponseTimeout` budget while each request remains capped by
`StorageTimeout`. Callerless durability retries have an explicit unbounded aggregate owner, bounded
provider calls, backoff, and a runtime/recovery lifecycle; they never inherit an abandoned caller's
cancellation token. Shutdown preparation is governed by the explicit timeout passed to
`ShutdownAsync` (or `ShutdownTimeout` during disposal), while work already admitted before caller
abandonment remains owned until it reaches a terminal runtime state.

Persistence work runs through bounded, single-purpose services. Typed,
immutable requests cross the `WalRuntimeService`, `FlushRuntimeService`, and
`CompactionRuntimeService` boundaries; the coordinator remains the sole owner
of mutable runtime state. `ImmutableFlushPipeline` owns ordered worker
scheduling, completion observation, retry backoff, and quiescence while the
coordinator applies the resulting state transitions. Manifest publication,
garbage collection, and cloud operations use the same bounded-worker model.
`CommitValidator` owns assertion, insert-only, point/range conflict, and stale
column-family validation.

Those services depend on narrow storage ports (`ILocalWalStore`, `ILocalFlushStore`,
`ILocalCompactionStore`, `IStorageReadStore`, and `IHybridCacheStore`) instead of the concrete local
store. `LocalDiskStore` remains the format authority and supplies those capabilities without
leaking its full surface into each runtime subsystem.

Hybrid-cache eviction is decided per SST. A local copy remains until its remote object identity is
verified and the manifest makes that object authoritative. Once proven, unrelated garbage
collection or WAL-retirement residue may degrade health and remain pending cleanup, but it does not
block eviction of that SST or other proven files. An upload, identity, or manifest outcome that is
unknown keeps the local copy available for recovery.

Concurrent local `Sync` or `Buffered` commits with the same durability may be
admitted as one coordinator batch. Each transaction remains a distinct atomic
Midge WAL frame and is validated in commit order. Logical visibility and
successful acknowledgements remain staged until the shared append completes;
`Sync` groups additionally wait for the shared filesystem sync. A failed
durability boundary therefore does not expose rejected writes.
`CommitCoalescer` owns eligibility, preflight, the single physical group append,
and apply mechanics; the coordinator owns failure ordering and acknowledgement.
One failed strict-conflict member does not invalidate disjoint members.

`RuntimeTelemetry` is the per-database owner of mutable counters. Runtime
services report WAL, flush, compaction, cloud, recovery, conflict, and read-path
events there; live queue and storage gauges are sampled from their owning
services. `PantsDiagnostics` publishes the representative process-wide
`ActivitySource` and `Meter` signals without storage paths or credentials.
Callers that need one-query detail can use
`IPantsTransaction.GetWithDiagnosticsAsync` to receive the value plus the SST,
level, cache, bloom, data-block, and cloud-hydration decisions for that read.

Flush and compaction publication uses durable intent records followed by
manifest edits. Immutable snapshots pin obsolete SSTs conservatively. GC only
removes those files after the final transaction or scan snapshot is released.
For provider-cloud databases, a successor may take the writer lease, compact,
and collect the old remote SST object while a fenced predecessor still has an
open transaction. The predecessor's snapshot continues to read its pinned local
cache view; losing write authority does not invalidate reads from that snapshot.
Disposing the transaction releases its local snapshot pin.

Transactions and scans must be disposed to release their snapshot pins and any
transaction spill files. Pants does not evict a live snapshot or use garbage
collection to release it; disposing an active transaction rolls it back.
Each scan captures the transaction's resident intent references and current
spill-run references at open time. Those snapshot arrays are reserved from the
transaction memory pool until the scan is disposed. If the reservation does not
fit, scan creation fails with `ResourceLimit` before registering a database
snapshot; point reads inspect intents under the transaction gate without making
a persistent read-view copy. An open scan therefore uses part of the same pool
that admits later writes and may cause them to spill sooner.
`PantsRuntimeConfiguration.LongLivedSnapshotWarningThreshold` defaults to one
hour; when runtime metrics are refreshed, snapshots at or beyond that age are
counted in `PantsRuntimeMetrics.LongLivedSnapshots` and each newly long-lived
snapshot increments the `pants.snapshots.lifetime_warnings` meter once.
Warnings do not cancel work or release pins. Hosts can alert on the meter and
use `OldestSnapshotAgeSeconds` to diagnose snapshots that hold old storage
state. `PantsDatabaseOptions.LongLivedSnapshotWarningThreshold` exposes the same
setting through dependency injection.

Cloud leasing is separated from provider transport: `CloudLeaseCoordinator`
implements expiry, conditional takeover, renewal, and fencing over
`ICloudLeaseStore`. `CloudObjectLeaseStore` persists the canonical cloud lease
record through conditional object operations, allowing provider clients to supply
ETag or generation semantics without entering the runtime layer.

An unknown renewal result fences its local holder immediately. The coordinator
then reconciles that exact renewal within a window capped by monotonic lease
time and its proposed expiry plus clock-skew tolerance, conditionally expiring
it if it becomes visible late. Reconciliation stops when a newer epoch appears
or the coordinator is disposed; provider failures that prevent a safe
determination remain `LeaseIndeterminate`.

Provider selection is also outside the runtime. Public `IPantsCloudProvider` and
`IPantsCloudObjectStore` contracts live in `Cntryl.Pants.Abstractions`; built-in providers live in
`Cntryl.Pants.Core`. A third-party provider opens its object-store primitive through the same async
SPI, while Pants retains ownership of object layout, WAL/SST formats, leases, fencing, and recovery.

Local, simulated-cloud, and provider-cloud writers use the same `LeaseTimeToLive` and
`LeaseClockSkewTolerance` profile. The 30-second default TTL retains the provider-cloud default
and aligns local takeover with current Midge; older Pants builds used an independent 60-second
local takeover delay. A local `.midge_leader` record becomes eligible for takeover once its age is
at or beyond `TTL + skew` (format/lease.md), matching Midge; a cloud lease document keeps the exact
`expires_at + skew` boundary held and becomes eligible on the first clock tick after it, also
matching Midge. The local record is parsed strictly: it must be UTF-8, `acquired_at` must be an
RFC 3339 timestamp with an explicit offset, and `epoch`/`checksum` must be plain decimal digits;
anything else is `LeaseIndeterminate`. Heartbeats run at one third of TTL, bounded between
1 ms and 10 seconds. Expiry only makes a successor eligible: every renewal, publication, and
release still validates the writer epoch/owner token, so a resumed old owner remains fenced.
Provider metadata mirrors, compaction intent writes, and DDL registry mutations re-read the shared
lease identity immediately before each conditional control-object write, so ownership changed after
the current object was read is rejected before the CAS. Provider SST uploads also re-read the shared
lease immediately before each conditional object create. Provider SST garbage collection revalidates
the remote lease after checking retention guards and immediately before each version-conditional
delete. WAL catalog recovery repairs, mirror convergence, epoch fencing, batch publication, and
retirement revalidate the lease before each conditional catalog write. The pre-acquisition epoch
floor lookup reads both catalog copies without repairing or converging them. WAL retirement also
revalidates before deleting each retired segment. When the stored lease has moved to a successor,
validation stops the pending write or delete. Cloud startup revalidates before creating its local
cache root and before staging each remote metadata or whole-object WAL copy into that cache.

## Shutdown lifecycle and error severity

The database lifecycle is monotonic: Open, then Closing, then Closed. A shutdown attempt that fails
because it was blocked (`Busy`) or timed out (`Timeout`) may be retried. Any other failure is
permanent: the database stays closing, new work is rejected with `Busy`, and the same exception is
replayed to every later `ShutdownAsync` or `DisposeAsync` caller without re-running shutdown. Calls
after a completed close also return `Busy`. This matches the reference engine and keeps the writer
lease and `LOCK` held, because a failed shutdown cannot prove its workers have stopped.

Shutdown stops new background scheduling before waiting for started worker loops. If a worker
misses its disposal deadline, or cleanup fails while that worker is still active, shutdown reports
`Timeout` and retains the local `LOCK`, local store, cloud lease, and cloud lease heartbeat. The
heartbeat is stopped and the owned resources are released only after every active worker loop has
exited. This prevents a cloud lease from expiring while an old worker can still complete a durable
provider operation; the next owner can then acquire the lease and recover the acknowledged state.

`PantsException.Severity` (`PantsErrorSeverity`) is the single classification for hosts deciding
whether to retry, back off, step down or halt. Caller errors are not retryable; transient errors may
succeed later; backpressure clears when a bounded resource frees; fenced means this writer lost
authority; defect errors (including `ResourceLimit`) are reported rather than retried; fatal errors
mean durable state cannot be trusted. Storage verification maps severity onto operator outcome
classes and `midge verify` exit codes; see [Storage Verification Outcomes](../storage-verification.md).

Classification follows Midge at the edges. A missing file or directory surfaced by storage I/O is a
transient `Io` failure, not a caller `NotFound`. A zero deadline passed to
`IPantsPersistentStorage.VerifyAsync` or `ShutdownAsync` is an already-expired budget and reports
`Timeout` (shutdown still begins and may be retried); only a negative deadline is
`InvalidArgument`. The `ShutdownTimeout` open option, which has no Midge counterpart and bounds
disposal, must still be greater than zero.
