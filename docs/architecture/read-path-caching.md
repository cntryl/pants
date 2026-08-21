# Read-Path Caching and Amplification

Pants divides the block-content cache into 16 independently synchronized shards, bounded by the block-cache allocation derived from `PantsMemoryBudget`. Each shard uses a concurrent entry map and serializes only policy bookkeeping, insertion, and eviction. LRU, TinyLFU, and CLOCK-Pro implement one stale-key-tolerant policy interface; the cache remains the source of truth.

The reader cache is separate. It retains parsed SST metadata and an open random-access file handle, while the block cache owns copies of decoded data blocks. Their hit and miss counters are reported independently. Point reads use positional I/O for uncached blocks. Streaming scans use contiguous reads and never insert into or evict from the block cache, preventing one-pass pollution.

Pants intentionally does not port Midge's experimental admission counter. The pinned Midge production path does not wire that counter into insertion either, so adding an admission gate would change default behavior. TinyLFU still makes frequency-aware eviction decisions after ordinary first-access insertion.

Every data block has one persisted double-hashed bloom filter. The manifest/SST key range is the cheaper coarse gate: an out-of-range lookup opens no reader and performs no bloom or data-block I/O. In-range bloom checks are classified as true positive, false positive, or true negative. Pants does not add a second SST-level bloom.

The read-amplification budget matches Midge: at most five SSTs and twenty blocks per read. Violations are counted in `PantsReadAmplificationMetrics`. When background compaction is enabled, a violation immediately submits a forced compaction through the serialized compaction worker and increments `CompactionTriggersTotal`; disabling background compaction preserves the diagnostic signal without mutating layout.
