# Runtime Architecture

`PantsActor` is the single state-transition coordinator. It owns the current
immutable `DatabaseSnapshot` publication and admits commands through a bounded
channel. Reads use frozen snapshots; mutations are serialized by the
coordinator.

Persistence work runs through bounded, single-purpose workers for WAL, flush,
compaction, manifest publication, garbage collection, and cloud operations.
`CommitValidator` owns assertion, insert-only, point/range conflict, and stale
column-family validation. `RuntimeTelemetry` is the per-database collection
point for metrics and read-path diagnostics.

Concurrent local `Sync` commits may be admitted as one coordinator batch. Each
transaction remains a distinct atomic Midge WAL frame and is validated in
commit order. The runtime withholds all successful acknowledgements until the
shared filesystem sync completes; one failed strict-conflict member does not
invalidate disjoint members.

Flush and compaction publication uses durable intent records followed by
manifest edits. Immutable snapshots pin obsolete SSTs conservatively. GC only
removes those files after the final transaction or scan snapshot is released.

Cloud leasing is separated from provider transport: `CloudLeaseCoordinator`
implements expiry, conditional takeover, renewal, and fencing over
`ICloudLeaseStore`. `CloudObjectLeaseStore` persists the Midge lease document
through conditional object operations, allowing provider clients to supply
ETag or generation semantics without entering the runtime layer.
