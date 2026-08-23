# Versioned Read Path Evidence

Issue #33 moves ordinary read-only transaction begin, version reads, and disposal off the coordinator while retaining explicit managed pins for SST lifetime and scans. Measurements used .NET 10.0.11 on the same Apple M5 machine.

An internal regression test records the coordinator admission counter before begin and asserts that begin, a missing-key point read, and disposal enqueue zero commands. Detailed point-read diagnostics, hybrid hydration, scans, and deferred obsolete-file collection retain coordinated paths where they own mutable or external state.

## YCSB-C

The baseline was collected before immutable database versions and the direct read path. The candidate `ShortRun` used three measured iterations with the same 50,000-key dataset and 1,000 logical operations per invocation.

| Storage | Clients | Baseline | Candidate | Baseline allocated | Candidate allocated |
|---|---:|---:|---:|---:|---:|
| Memory | 1 | 656.7 us/op | 398.4 ns/op | 2.68 MB/op | 1.00 KB/op |
| Memory | 16 | 2,917.7 us/op | 607.5 ns/op | 2.68 MB/op | 1.00 KB/op |
| Memory | 64 | 3,161.8 us/op | 639.5 ns/op | 2.68 MB/op | 1.02 KB/op |
| Local | 1 | 710.2 us/op | 435.0 ns/op | 2.69 MB/op | 1.20 KB/op |
| Local | 16 | 2,668.2 us/op | 712.6 ns/op | 2.69 MB/op | 1.21 KB/op |
| Local | 64 | 2,802.5 us/op | 760.6 ns/op | 2.69 MB/op | 1.22 KB/op |
| Simulated cloud | 1 | 705.7 us/op | 4,006.8 ns/op | 2.69 MB/op | 2.91 KB/op |
| Simulated cloud | 16 | 2,705.0 us/op | 1,253.4 ns/op | 2.69 MB/op | 2.82 KB/op |
| Simulated cloud | 64 | 2,775.1 us/op | 1,154.9 ns/op | 2.69 MB/op | 2.84 KB/op |

The 16- and 64-client rows no longer collapse behind serialized begin/read/dispose commands. Hybrid and simulated-cloud rows keep coordinator work when hydration is required; their concurrent throughput still scales instead of degrading.

## Tier 3 engine reads

The issue baseline recorded approximately 87-90 us for Tier 3 engine point reads. The candidate statistical run measured 13.99 us for local storage and 25.02 us for simulated cloud. These rows retain storage telemetry and cache behavior; they are not memory-only shortcuts.
