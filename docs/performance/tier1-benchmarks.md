# Tier 1 Benchmarks

Tier 1 measures isolated hot paths. It is intended for local regression analysis, not absolute performance claims or Rust-versus-C# comparisons. Setup, fixture generation, file creation, and cleanup stay outside measured methods. Batched rows declare `OperationsPerInvoke` so BenchmarkDotNet reports cost per logical operation.

## Midge Mapping

| Midge family | Pants fixture | Measured Pants path |
| --- | --- | --- |
| `tier1_hotpath_api` | `ApiBenchmarks` | In-memory transaction get and best-effort put |
| `tier1_hotpath_bloom` | `BloomBenchmarks` | SST point-read bloom decision |
| `tier1_hotpath_block_cache` | `BlockCacheBenchmarks` | LRU cache hit, miss, and 4 KiB insert |
| `tier1_hotpath_iterator` | `IteratorBenchmarks` | Ordered memtable traversal and seek |
| `tier1_hotpath_memtable` | `MemtableBenchmarks` | Ordered MVCC map get, put, and traversal |
| `tier1_hotpath_sst` | `SstBenchmarks` | SST v4 encode and decode |
| `tier1_hotpath_trie` | `TrieBenchmarks` | Trie lookup, encode, and decode |
| `tier1_hotpath_wal` | `WalBenchmarks` | WAL record encode and decode |
| `tier1_hotpath_tlv_encoding` | `TlvBenchmarks` | Production WAL TLV encoding at fixed payload sizes |
| `tier1_hotpath_singleflight` | `SingleflightBenchmarks` | Runtime response fan-out to 1, 4, 16, and 64 waiters |
| `tier1_hotpath_compression` | `CompressionBenchmarks` | LZ4 and Zstd3 block compression and decompression |
| `tier1_hotpath_event_loop` | `EventLoopBenchmarks` | Single-reader/single-writer channel dispatch |

## Running

Build and list the supported rows:

```sh
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release -- --list flat
```

Run all Tier 1 rows or one family:

```sh
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release -- --filter '*Tier1*'

dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release -- --filter '*WalBenchmarks*'
```

Use `--job Dry` only to validate benchmark lifecycle. Do not use dry-run measurements as performance evidence. Investigate repeatable movement greater than 5% against a same-machine saved baseline, matching Midge's Tier 1 posture.
