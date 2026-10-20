# Pants and Midge v0.1.1 scenario mapping

This table maps each Pants BenchmarkDotNet class to the closest Midge v0.1.1 stress benchmark family. A family match identifies related work; it does not prove that two rows use the same logical operation, inputs, storage mode, clients, operation count, or measurement window. No pair is approved for a direct ratio by this map alone.

| Tier | Pants BDN row family | Midge `benchmark_id` family | Mechanics assessment |
| --- | --- | --- | --- |
| 1 | `ApiBenchmarks.*` | `tier1_hotpath_api::*` | Related transaction API calls; operation boundaries and read/write setup differ. |
| 1 | `BlockCacheBenchmarks.*` | `tier1_hotpath_block_cache::*` | Related hit, miss, and insert operations; cache size and batch shapes differ. |
| 1 | `BloomBenchmarks.*` | `tier1_hotpath_bloom::*` | Related bloom decisions; key populations and probe batches differ. |
| 1 | `CompressionBenchmarks.*` | `tier1_hotpath_compression::*` | Compare only matching codec and payload shapes; measured framing differs by row. |
| 1 | `EventLoopBenchmarks.*` | `tier1_hotpath_event_loop::*` | Related dispatch paths; thread and queue boundaries differ. |
| 1 | `IteratorBenchmarks.*` | `tier1_hotpath_iterator::*` | Related ordered traversal and seek operations; fixture layouts differ. |
| 1 | `MemtableBenchmarks.*` | `tier1_hotpath_memtable::*` | Related get, put, delete, and traversal operations over different engine data structures. |
| 1 | `SingleflightBenchmarks.CompleteWaiters` | `tier1_hotpath_keyed_group_commit::*` | Not equivalent: Pants completes runtime response waiters; Midge measures a keyed accumulator's join/rotate/complete path. |
| 1 | `SstBenchmarks.*` | `tier1_hotpath_sst::*` | Related encode/decode operations over different SST formats and fixtures. |
| 1 | `TlvBenchmarks.*` | `tier1_hotpath_tlv_encoding::*` | Related TLV fields; payload sizes and surrounding record work must match before comparison. |
| 1 | `TrieBenchmarks.*` | `tier1_hotpath_trie::*` | Related lookup and prefix operations; key distributions differ. |
| 1 | `WalBenchmarks.*` | `tier1_hotpath_wal::*` | Related record encode/decode operations; record framing and payload fixtures differ. |
| 2 | `BlockCacheSubsystemBenchmarks.*` | `tier2_subsystem_block_cache::*` | Related cache rotation and eviction; entry counts and cache state differ. |
| 2 | `BloomBuildSubsystemBenchmarks.*` | `tier2_subsystem_bloom_build::*` | Related filter construction; key counts align by case, but implementation and measurement harness differ. |
| 2 | `CloudWalBatchingSubsystemBenchmarks.*` | — | No Midge v0.1.1 target measures Pants' simulated cloud WAL publication batch. |
| 2 | `CompressionSubsystemBenchmarks.*` | `tier2_subsystem_compression::*` | Related batched codec work; compare only matching codec, block count, and block size. |
| 2 | `DurabilityCommitLatencySubsystemBenchmarks.*` | `tier2_subsystem_durability_commit_latency::*` | Related durability modes and writer counts; commit grouping and storage setup differ. |
| 2 | `EventLoopSubsystemBenchmarks.*` | `tier2_subsystem_event_loop::*` | Related multi-thread dispatch; scheduler and queue setup differ. |
| 2 | `IteratorMultiSstSubsystemBenchmarks.*` | `tier2_subsystem_iterator_multi_sst::*` | Related multi-run iteration; run counts and key overlap must match per case. |
| 2 | `MemtableGenerationRotationSubsystemBenchmarks.*`, `MemtableRotateSubsystemBenchmarks.*` | `tier2_subsystem_memtable_rotate::*` | Related rotation behavior; Pants separates generation and rotation comparisons. |
| 2 | `RangeScanCacheSubsystemBenchmarks.*` | `tier2_subsystem_range_scan_cache::*` | Related warm/cold range cache access; cache state and query widths differ. |
| 2 | `ReadAmplificationSubsystemBenchmarks.*` | `tier2_subsystem_read_amplification::*` | Related read amplification probes; SST layout and candidate counts differ. |
| 2 | `TransactionLatencySubsystemBenchmarks.*` | `tier2_subsystem_transaction_latency::*` | Related transaction lifecycle latency; transaction shapes and sample windows differ. |
| 3 | `EngineSystemBenchmarks.*` | `tier3_system_engine::*` | Related engine point reads; fixture keys and available storage modes differ. |
| 3 | `LifecycleSystemBenchmarks.*` | `tier3_system_lifecycle::*` | Related reopen and flush-cycle boundaries; setup state and work per sample differ. |
| 3 | `MvccSystemBenchmarks.*` | `tier3_system_mvcc::*` | Related old-version reads and writes; snapshot age and operation batches differ. |
| 3 | `ScanSystemBenchmarks.*` | `tier3_system_scan::*` | Related LSM scan layouts; first-row boundaries and fixture contents differ. |
| 3 | `SstSystemBenchmarks.*` | `tier3_system_sst::*` | Related SST point/range seeks; index formats and fixture sizes differ. |
| 3 | `WalReplayScalingSystemBenchmarks.*` | `tier4_system_recovery_throughput::*` | Related recovery work, but scale axes and measured boundaries differ; not an equivalent row. |
| 4 | `CompactionBackpressureSystemBenchmarks.*` | `tier4_system_compaction_backpressure::*` | Related backpressure workloads; batch sizes and observation windows differ. |
| 4 | `CompressionPolicySystemBenchmarks.*` | `tier4_system_compression_policy::*` | Compare only matching compression goal and data shape; fixed work and report units differ. |
| 4 | `LocalThroughputSystemBenchmarks.*` | `tier4_system_local_throughput_regression::*` | Related local/memory write throughput; batch sizes and run duration differ. |
| 4 | `RecoveryThroughputSystemBenchmarks.*` | `tier4_system_recovery_throughput::*` | Related recovery states; state preparation and per-sample work differ. |
| 4 | `StreamingSystemBenchmarks.*` | `tier4_streaming_workload::*` | Related streaming workloads; Pants uses fixed operation counts while Midge smoke uses its profile windows. |
| 4 | `StrictGroupCommitSystemBenchmarks.CompleteLocalSystemAsync` | `tier4_system_strict_group_commit::*` | Closest end-to-end match; writer count, flush waves, transactions per writer, and verification work differ. |
| 4 | `YcsbAWorkloadBenchmarks.*` | `tier4_ycsb_workload_a::*` | Same 50/50 read/update family; key distribution and sample windows differ. |
| 4 | `YcsbBWorkloadBenchmarks.*` | `tier4_ycsb_workload_b::*` | Same 95/5 read/update family; key distribution and sample windows differ. |
| 4 | `YcsbCWorkloadBenchmarks.*` | `tier4_ycsb_workload_c::*` | Same read-only family; key distribution and sample windows differ. |
| 4 | `YcsbDWorkloadBenchmarks.*` | `tier4_ycsb_workload_d::*` | Same read-latest/update family; key distribution and sample windows differ. |
| 4 | `YcsbEWorkloadBenchmarks.*` | `tier4_ycsb_workload_e::*` | Same short-range scan/update family; range and sample windows differ. |
| 4 | `YcsbFWorkloadBenchmarks.*` | `tier4_ycsb_workload_f::*` | Same read-modify-write family; client scheduling and sample windows differ. |

Midge v0.1.1 also has `tier4_bounded_compaction_qualification::*`, which has no Pants BDN counterpart. The similarly named `CompactionBackpressureSystemBenchmarks` measures a different qualification boundary and is not a substitute.

The issue's example pairing of Midge `tier1_hotpath_keyed_group_commit` with Pants `StrictGroupCommitSystemBenchmarks` is not equivalent: the former measures an in-memory accumulator, while the latter includes durable transaction commits, flush, compaction, shutdown, reopen, and data verification. The correct system-level Midge family is `tier4_system_strict_group_commit`, with the differences listed above.

The aggregate report keeps these results side by side and omits ratios. Qualify a narrower pair only after matching its logical unit, workload parameters, storage mode, client count, operation count, and measurement class on the same host.
