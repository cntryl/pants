# Immutable Database Version Evidence

Issue #32 replaces copied transaction snapshots with retained immutable database versions. Measurements below were collected on the same Apple M5 machine from `main` at `bbdef0e` and the candidate branch, using .NET 10.0.11.

## Transaction begin

The Tier 3 MVCC fixture contains 50,000 keys. A BenchmarkDotNet `Dry` run executes each row once and is cold lifecycle evidence, not a statistical latency claim.

| Storage | Main allocated | Candidate allocated | Main mean | Candidate mean |
|---|---:|---:|---:|---:|
| Local | 2,748.85 KB | 12.29 KB | 2,840.50 us | 1,769.58 us |
| Simulated cloud | 2,747.85 KB | 12.29 KB | 53,267.00 us | 2,357.00 us |

The remaining allocation includes the transaction, coordinator command, and disposal path. Snapshot acquisition itself retains the already-published `DatabaseVersion`; it does not enumerate keys. Cardinality tests verify that version publication allocation remains constant from 1,000 to 50,000 keys and that a single-key update at 50,000 keys performs less than 16 KB of path-copy allocation.

A `ShortRun` statistical check completed all four MVCC rows. Transaction begin averaged 26.656 us locally and 26.601 us with simulated cloud storage, allocating 11.83 KB end to end. Old-version reads remained correct across all measured iterations.

## Ordered root tradeoff

The same-machine Tier 1 `ShortRun` comparison measured the old mutable `SortedDictionary` against the candidate Microsoft `ImmutableSortedDictionary` root:

| Operation | Main | Candidate | Main allocated | Candidate allocated |
|---|---:|---:|---:|---:|
| Get hit | 47.300 ns | 48.853 ns | 0 B | 0 B |
| Get miss | 52.677 ns | 43.962 ns | 0 B | 0 B |
| Iterate 100 | 6.683 ns/op | 5.703 ns/op | 3 B/op | 1 B/op |
| Put | 45.864 ns | 101.622 ns | 48 B | 656 B |

Lookup and ordered iteration do not regress materially. A point update becomes slower and allocates a bounded path copy, which is the explicit tradeoff that removes the O(total keys) copy from every transaction begin and snapshot publication. No custom persistent tree is justified by these results.
