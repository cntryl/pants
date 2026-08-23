# Grouped Commit Evidence

Issue #34 extends the existing bounded commit coalescer to BestEffort and CloudAsync without pooling command completions or adding dependencies. CloudStrict remains isolated. CloudAsync groups append at the existing local Buffered WAL boundary, publish one immutable database version, complete callers, and schedule cloud sealing through the existing controller.

Measurements used .NET 10.0.11 on the same Apple M5 machine. Candidate and immediate pre-change results are BenchmarkDotNet `ShortRun` jobs with three measured iterations. Rows marked noisy by BenchmarkDotNet are not used to justify the change.

## Write-heavy workloads

| Workload | Storage | Clients | Pre-change | Candidate | Pre-change allocated | Candidate allocated |
|---|---|---:|---:|---:|---:|---:|
| YCSB A | Hybrid | 16 | 79.74 us/op | 62.26 us/op | 31.31 KB/op | 24.34 KB/op |
| YCSB A | Simulated cloud | 64 | 90.68 us/op | 77.77 us/op | 28.77 KB/op | 23.54 KB/op |
| YCSB F | Hybrid | 16 | 178.91 us/op | 142.00 us/op | 60.76 KB/op | 54.61 KB/op |
| YCSB F | Hybrid | 64 | 185.10 us/op | 132.41 us/op | 60.45 KB/op | 46.20 KB/op |
| YCSB F | Simulated cloud | 16 | 152.61 us/op | 125.86 us/op | 60.91 KB/op | 51.45 KB/op |
| YCSB F | Simulated cloud | 64 | 157.85 us/op | 130.60 us/op | 60.71 KB/op | 48.24 KB/op |

The original issue baseline allocated 3.7-5.6 MB/op for YCSB A and 5.6-10.7 MB/op for YCSB F. The immutable-version and grouped-commit work together reduce the measured candidate rows to 6.88-60.91 KB/op, exceeding the 90% allocation-reduction target without completion pooling.

## Sync durability guardrail

The one-writer Sync row remains effectively flat at 4.158 ms/op before the change and 4.094 ms/op after it. The 64-writer row measured 1.508 ms/op before and 1.193 ms/op after. The three-iteration 16-writer candidate was noisy at 2.994 ms/op versus a one-iteration 2.572 ms/op pre-change measurement; the implementation does not change Sync eligibility, WAL framing, or acknowledgement boundaries.

Correctness tests additionally force eight BestEffort commits to publish with zero WAL appends and eight CloudAsync commits to publish through one local WAL append with zero fsyncs. Full WAL, recovery, fencing, cloud crash, cancellation, shutdown, and failure-injection coverage remains the release gate.
