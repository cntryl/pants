# Immutable Memtable Generation Evidence

Issue #35 replaces filter/copy/remove memtable rotation with storage-owned per-column-family generations. Rotation detaches the retained family list directly, while sequence-based rollback preserves the exact mutable suffix across single and grouped WAL failures. Existing frozen-flush plans, manifest publication, retry retention, snapshot-aware garbage collection, and cloud upload tracking remain unchanged.

SST v4 output now streams directly to the staging file through an incremental CRC32C stream. Data blocks remain bounded at 64 KB. The implementation deliberately does not add buffer pooling: removing the full-file materialization produced the measured gain, while pooling would add lifecycle complexity without separate evidence. Golden tests compare exact streaming and in-memory bytes plus CRCs for Latency, Throughput, and Economy goals.

Measurements used .NET 10.0.11 on the same Apple M5 machine with BenchmarkDotNet `ShortRun` and three measured iterations.

## Rotation subsystem

The benchmark interleaves 8,192 mutations across 32 column families and rotates one 256-operation family.

| Rotation | Mean | Allocated |
|---|---:|---:|
| Filter, copy, and remove | 45.65 us | 2,144 B |
| Detach immutable generation | 451.5 ns | 0 B |

Detachment is approximately 100 times faster in this bounded handoff and allocates nothing.

## Write and flush

| Storage | Pre-change | Candidate | Pre-change allocated | Candidate allocated |
|---|---:|---:|---:|---:|
| Local | 48.29 ms | 48.35 ms | 851.58 KB | 824.91 KB |
| Simulated cloud | 97.21 ms | 99.16 ms | 5,466.51 KB | 4,459.69 KB |

Latency is within ShortRun variance; local allocation falls 3.1% and simulated-cloud allocation falls 18.4%.

## Compression and compaction

The 512-operation Tier 4 rows retain exact compression policy and bytes. Representative results:

| Goal and shape | Pre-change | Candidate | Pre-change allocated | Candidate allocated |
|---|---:|---:|---:|---:|
| Latency, structured | 825.0 us/op | 781.9 us/op | 856.61 KB/op | 697.47 KB/op |
| Throughput, repeated | 906.2 us/op | 739.6 us/op | 834.69 KB/op | 834.36 KB/op |
| Throughput, mixed | 897.5 us/op | 768.6 us/op | 834.78 KB/op | 834.38 KB/op |
| Economy, mixed | 855.0 us/op | 731.7 us/op | 787.76 KB/op | 787.52 KB/op |

The streaming path removes about 159 KB per operation from Latency-policy rows and improves representative Throughput/Economy latency by 14-18%. No persisted-format, durability, recovery, fencing, or dependency changes are involved.
