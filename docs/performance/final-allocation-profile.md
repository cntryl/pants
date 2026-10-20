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

## Same-machine repeatability check

After the metric-race fix, the affected YCSB A memory/16-client row was run three times with the
normal BenchmarkDotNet job on the same Apple M5, .NET 10.0.11 machine at commit
`73cd29f332bf0a71c0963c0a6e53b75c99cfd93b` (2026-10-20). Each run used 50,000 initial keys and
1,000 logical operations per invocation.

The runs used the default job and were executed sequentially with separate artifact directories:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- \
  --filter '*YcsbAWorkloadBenchmarks.RunAsync*Memory-16*' --exporters csv \
  --artifacts /tmp/pants-38-ycsb-a-memory16-normal-runN
```

`N` was `1`, `2`, and `3` for the three runs.

| Run | Mean | StdDev | 99.9% error | Allocated |
| --- | ---: | ---: | ---: | ---: |
| 1 | 6.479 us/op | 1.813 us | 0.6216 us | 9.25 KB/op |
| 2 | 4.826 us/op | 0.5795 us | 0.2091 us | 8.68 KB/op |
| 3 | 4.514 us/op | 0.0907 us | 0.0884 us | 9.32 KB/op |

The first run was multimodal, and the run means vary enough that these results do not support a
throughput improvement claim. Allocations stayed within 8.68–9.32 KB/op. This is a repeatability
check for the corrected row, not evidence that the metrics fix removed allocations. No leaf-path
allocation optimization was promoted without a system-level gain and a matching ownership-safe
change.

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
