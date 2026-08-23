# Runtime Architecture

`PantsActor` is the single state-transition coordinator. It owns the current
immutable `DatabaseSnapshot` publication and admits commands through a bounded
channel. Reads use frozen snapshots; mutations are serialized by the
coordinator.

Persistence work runs through bounded, single-purpose services. Typed,
immutable requests cross the `WalRuntimeService`, `FlushRuntimeService`, and
`CompactionRuntimeService` boundaries; the coordinator remains the sole owner
of mutable runtime state. `ImmutableFlushPipeline` owns ordered worker
scheduling, completion observation, retry backoff, and quiescence while the
coordinator applies the resulting state transitions. Manifest publication,
garbage collection, and cloud operations use the same bounded-worker model.
`CommitValidator` owns assertion, insert-only, point/range conflict, and stale
column-family validation.

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
