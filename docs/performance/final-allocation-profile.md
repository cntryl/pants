# Final allocation profile

Issue #38 reran the full 157-scenario practical suite after the structural performance work in
issues #32 through #37. The first run exercised all scenarios but correctly returned failure because
`YcsbA Memory-16` exposed a concurrent snapshot-metrics race.

## Correctness finding

`GetOldestSnapshotAgeSeconds` previously read `ActiveSnapshotCount` and then used LINQ `Max` over a
second enumeration. Direct read-only snapshots can close concurrently, so the sequence could become
empty between those operations. The replacement performs one allocation-free pass over each
snapshot collection and never derives correctness from a stale count.

The focused regression opens and closes 4,000 direct read-only snapshots across 16 tasks while
reading metrics 1,000 times. The affected Tier 4 benchmark now completes:

| Scenario | Before | After | After allocation |
| --- | ---: | ---: | ---: |
| YCSB A, memory, 16 clients | failed | 12.15 us/op | 7.02 KB/op |

The other five YCSB A Dry rows remained within cold-run noise. No allocation reduction is claimed
for those rows.

## Retained allocations

The practical profile still shows allocations that are required by current ownership and safety
contracts:

- API inputs are copied when a transaction takes ownership; caller buffers remain mutable.
- returned values and scan entries do not alias mutable engine state.
- immutable database versions retain snapshot isolation across concurrent transactions.
- WAL, SST, manifest, and cloud payloads retain exact persisted bytes for checksums, retries, and
  authoritative readback.
- provider request signing and credential/token data remain request-scoped and are not pooled.
- compression remains on the pinned .NET 10-compatible implementation; native .NET 11 Zstd work is
  tracked separately by issue #18.

Candidates for generalized pooling or unsafe zero-copy APIs were rejected because the system rows
did not provide evidence that their additional lifetime complexity would pay for itself. No new
dependency or buffer pool was added.
