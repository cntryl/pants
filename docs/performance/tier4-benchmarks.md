# Tier 4 complete-system benchmarks

Tier 4 owns complete-system and workload guardrails. Pants maps the pinned Midge inventory to streaming local/cloud traffic, compaction backpressure, compression policy across three goals and five data shapes, memory-versus-local batch throughput, recovery after flush/compaction, strict group commit with reopen validation, and YCSB A–F over Midge's storage/client matrix.

Run all Tier 4 rows with:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj -c Release -- --filter "*Tier4*"
```

BenchmarkDotNet requires a fixed logical operation count for correct per-operation throughput and allocation normalization. Therefore sustained Midge duration windows are represented as deterministic fixed-work windows: 10,000 YCSB operations and 20,000 streaming operations. Storage modes, client counts, operation mixes, value sizes, durability, and lifecycle boundaries remain aligned with Midge. YCSB key selection uses a deterministic hot-key bias so runs remain reproducible; it is not an exact Zipfian sampler.

Collect at least three Release runs on the same machine, or five before accepting a material Tier 4 change. Investigate sustained throughput loss above 15% or repeated p99 growth above 20%. Dry runs validate lifecycle only. For cloud/hybrid rows, reject failed uploads, no-space stalls, or local usage above 100%; correlate reports with runtime health metrics.
