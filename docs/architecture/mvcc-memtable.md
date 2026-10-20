# MVCC Memtable Concurrency

Pants keeps the active column-family map in the runtime actor. Each family is
an `ImmutableSortedDictionary<byte[], CellState>`, not a mutable sorted
dictionary. A commit builds a replacement root with `SetItem` and installs it
in the actor-owned map; existing roots remain unchanged.

`PublishSnapshot` creates a `DatabaseVersion` that contains an immutable map of
those family roots. It shares each immutable tree rather than copying every key
and value. A transaction captures the published version at begin, and its
point reads and scans use that version plus its ordered private intents. A scan
therefore enumerates a stable root while later commits publish new roots. The
snapshot pin retains the referenced version across flush, compaction, and
column-family drop.

## Decision

Pants uses persistent immutable ordered roots for MVCC readers and keeps the
actor as the sole state-transition authority. It does not port Midge's
CAS-based lock-free skiplist: the current write path is serialized, while
immutable roots already let scans proceed safely across concurrent commits.
This choice does not make writes lock-free; it removes scan safety's dependence
on readers enumerating a mutable collection under the writer's serialization.

The [immutable database-version measurements](../performance/immutable-database-versions.md)
compare the previous `SortedDictionary` with the selected
`ImmutableSortedDictionary` under Pants' single-writer model. Lookup and
ordered iteration remained comparable; point updates became slower and
allocated a bounded path copy. The measured reduction in transaction-start
allocation and time justified that tradeoff. Revisit the choice if the runtime
introduces concurrent writers or new workloads change the measured balance.

The observable MVCC rule is that a transaction sees the database at its frozen
sequence and never sees a later commit. The concurrency tests keep a scan
enumerator active across a committed write and verify that it returns the old
root's values; sequence-boundary tests also verify old and new transactions see
their respective committed values.
