# SST Index Tuning

Pants profiles keys while writing each SST and applies Midge's pinned decision rule at finalization. The profile tracks adjacent shared-prefix average and maximum, four-byte prefix divergence, byte entropy, the prefix shared by all keys, key-length standard deviation, and the ten hottest tracked prefixes. Empty keys are excluded, matching Midge.

`Sparse` remains the historical on-disk name for the complete binary block-first-key index. Every SST persists that index. Structured SSTs additionally persist Midge's flat prefix-compressed trie and select it with metadata discriminant `1`; small, high-entropy, or highly divergent SSTs select the binary index with discriminant `0` and omit the trie footer handle.

Readers validate the metadata/footer combination, trie graph, child ordering, block IDs, and reconstructed keys against the canonical binary index before using a trie. The persisted choice is therefore self-describing and cannot silently change correctness: both formats retain the same ordered data blocks and block bloom filters.

Selection is intentionally write-time-only. There is no public configuration switch because Midge exposes this as an internal optimization, and keeping the tuner deterministic preserves cross-engine format behavior.
