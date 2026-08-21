# MVCC Memtable Concurrency

Pants intentionally keeps each column family's mutable state in an ordered
`SortedDictionary` owned exclusively by the coordinator. This differs from
Midge's lock-free skiplist because Pants has one serialized writer and never
allows a reader to enumerate that mutable collection.

Every committed state transition publishes a deep, immutable-by-ownership
`DatabaseSnapshot`. A transaction freezes the current snapshot when
`BeginTransactionAsync` returns. Point reads and scans use only that frozen
copy plus the transaction's ordered private intents. A scan therefore cannot
observe, contend with, or invalidate an enumeration while the coordinator
mutates the active memtable. Snapshot pins retain that same owned copy through
flush, compaction, and column-family drop.

This copy-on-publication design is the deliberate .NET substitute for Midge's
epoch-guarded concurrent skiplist. It favors a simple ownership proof under
the actor runtime over a lock-free structure whose CAS writers would provide
no benefit while all Pants mutations remain serialized. The tradeoff is the
allocation and copy cost of snapshot publication; telemetry and benchmarks
should guide any future persistent-tree replacement.

The observable MVCC rule remains the same: a transaction sees the database at
its frozen sequence and never sees a later commit. Tests exercise a pinned
scan while concurrent commits publish new snapshots and verify old and new
transactions observe their respective sequence boundaries.
