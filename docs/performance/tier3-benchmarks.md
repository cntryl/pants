# Tier 3 system benchmarks

Tier 3 measures complete engine paths over prepared durable layouts. The workload inventory originated from Midge at `c5ffc2d`; that SHA is historical. `EngineSystemBenchmarks` now follows the Midge 0.5.2 engine-read fixture at `c684310d9d8ce8ecd1151c47151d4bea98b348cf`, with matching key/value bytes, setup batches, memtable limit, compaction setting and value validation. See [current-midge-engine-reads.md](current-midge-engine-reads.md) for the remaining measurement differences. The other rows retain their historical workloads. Current persisted-format compatibility is documented separately in [LsmSpecFormat9.md](../compatibility/LsmSpecFormat9.md). The inventory covers rotating engine point reads, write/flush and clean-reopen lifecycle boundaries, old-version MVCC reads, prefix-scan first-row seeks over three LSM layouts, and SST point/range seeks. Every row runs against local and simulated-cloud storage.

Run the suite with:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj -c Release -- --filter "*Tier3*"
```

Use `--job Dry` only for discovery and lifecycle validation. Collect at least three normal Release runs on the same machine. Investigate a sustained throughput reduction above 15% or repeated p99 growth above 20%.

Database creation, durable-layout preparation, flush/compaction, key generation, and expected-value setup occur outside measured methods. Lifecycle rows intentionally include the named lifecycle boundary. Scan rows measure query construction through the first returned row and retain a pinned snapshot across invocations.
