# Tier 3 system benchmarks

Tier 3 measures complete engine paths over prepared durable layouts. The workload inventory originated from Midge at `c5ffc2d`; that SHA is the historical performance-comparison boundary, not the current compatibility baseline. Current behavior and persisted-format compatibility are pinned separately to `75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`. The inventory covers rotating engine point reads, write/flush and clean-reopen lifecycle boundaries, old-version MVCC reads, prefix-scan first-row seeks over three LSM layouts, and SST point/range seeks. Every row runs against local and simulated-cloud storage.

Run the suite with:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj -c Release -- --filter "*Tier3*"
```

Use `--job Dry` only for discovery and lifecycle validation. Collect at least three normal Release runs on the same machine. Investigate a sustained throughput reduction above 15% or repeated p99 growth above 20%.

Database creation, durable-layout preparation, flush/compaction, key generation, and expected-value setup occur outside measured methods. Lifecycle rows intentionally include the named lifecycle boundary. Scan rows measure query construction through the first returned row and retain a pinned snapshot across invocations.
