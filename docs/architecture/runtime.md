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

Cloud leasing is separated from provider transport: `CloudLeaseCoordinator`
implements expiry, conditional takeover, renewal, and fencing over
`ICloudLeaseStore`. `CloudObjectLeaseStore` persists the Midge lease document
through conditional object operations, allowing provider clients to supply
ETag or generation semantics without entering the runtime layer.

Provider selection is also outside the runtime. Public `IPantsCloudProvider` and
`IPantsCloudObjectStore` contracts live in `Cntryl.Pants.Abstractions`; built-in providers live in
`Cntryl.Pants.Core`. A third-party provider opens its object-store primitive through the same async
SPI, while Pants retains ownership of object layout, WAL/SST formats, leases, fencing, and recovery.

Local, simulated-cloud, and provider-cloud writers use the same `LeaseTimeToLive` and
`LeaseClockSkewTolerance` profile. The 30-second default TTL retains the provider-cloud default
and aligns local takeover with current Midge; older Pants builds used an independent 60-second
local takeover delay. The exact boundary remains held, and takeover becomes eligible on the first
clock tick after `last renewal + TTL + skew`. Heartbeats run at one third of TTL, bounded between
1 ms and 10 seconds. Expiry only makes a successor eligible: every renewal, publication, and
release still validates the writer epoch/owner token, so a resumed old owner remains fenced.
