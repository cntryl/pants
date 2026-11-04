# Shared SST read resources

This slice compares Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf` with the qualified Pants behavior baseline `071fc8491270c74a9540c5dc71d2348ca8f47a78`. Persisted FORMAT 9 and SST V5 bytes are unchanged.

## Effective configuration and ownership

Midge `engine/api/options.rs:1069–1184` reserves the transaction pool, at most one fifth for compaction, and two memtables. Automatic persistent memtables leave a quarter of the remaining allowance for reads. There is no additional top-level scan pool. Four bytes with default settings cannot supply a compaction pool and are rejected.

The final read allowance becomes one `SstReadResources` owner. As in Midge `runtime/read_resources.rs:186–195`, three quarters are block contents and one quarter is shared reader/open metadata. Both synchronous and asynchronous cache paths consume that same metadata allowance and source-handle limit. Idle readers may be reclaimed before refusal; a leased reader retains its claim even after cache removal. Metadata claims precede the native metadata read, decompression and parsing, then shrink to the retained representation after each parsed block. Trie traversal admits reconstructed prefixes before allocating them. An uncertain physical source close retains its claim rather than creating duplicate capacity.

Open coordination follows Midge `runtime/read_resources/admission.rs:49–107`: one through eight physical open owners, a metadata/8 coordination suballowance, coalescing by manifest identity, and shared capacity notifications. The coordination charge is also reserved from metadata. This fraction is not a second additive memory pool. The fixed392-byte compatibility coordination quantum, plus twice the canonical filename length, matches the pinned64-bit reference admission behavior. It is not CLR `sizeof` or an RSS measurement;32-bit and other platform equivalence are unqualified. Actual native and Pants controls refuse a cold point/scan at64KiB while succeeding at96KiB and128KiB. Startup validates one local SST at a time outside runtime cache/open admission, matching Midge `runtime/state/recovery.rs:757–806`; it retains the physical handle limit and codec bounds, inserts no cache entry, and allows Open to finish before a later runtime admission refusal.

`PantsResolvedConfiguration.ReadResourceBudgetBytes` is the total read allowance. `BlockCacheBytes` and `PantsRuntimeMetrics.BlockCacheCapacityBytes` are the actual content capacity. `ReadMetadataBudgetBytes` and runtime `ReadMetadataUsedBytes`, `ReadMetadataPeakBytes`, and `ReadMetadataCapacityBytes` report the shared metadata owner. Legacy `ScanMemoryPoolBytes` and `ScanBuffer*` are zero. These effective setting and diagnostic changes are intentional.

## Scan and spill lifetimes

Point reads and public scans share cached asynchronous readers. Trusted nonoverlapping level/sublevel runs open only files reached by the merge. Overlapping L0 runs and untrusted bounds continue to load range metadata before merging. Bounds include range-tombstone endpoints; a reader whose point stream is exhausted remains alive while its ranges can cover the merge frontier. This follows Midge `runtime/read_snapshot/scan.rs:39–47,278–355`.

Public scan payload buffers are **not** charged to the shared reader metadata allowance. Midge `sst/fs/reader_io/mod.rs:324–344,1002–1018`, `index.rs:190–225,237–273`, and `io.rs:465–470` retain format-bounded per-cursor blocks/windows independently. Pants retains its decoded data-block and entry-size bounds and opens a bounded number of active sources under metadata/handle admission. Returned rows remain caller-owned. Explicitly budgeted raw/compaction cursors retain separate working reservations, admitted before native block I/O and materialization. No global RSS bound follows from adding the configured pools.

Reverse spill chunk tables reserve from the transaction pool. When that reservation does not fit, the cursor scans checked sparse frames to locate the preceding chunk and retains only a fixed-stride chunk workspace. It returns the same rows and releases materialized table charges on disposal or failed construction. This follows Midge `runtime/transaction_spill/scan.rs:49–79` and `format.rs:800–811`.

## Executed evidence and limits

Behavioral red cases establish the previous content-capacity/tiny-budget/immediate-pin mismatches, missing native metadata admission/retention, duplicated scan reader ownership, eager cold limited-scan metadata, and reverse spill refusal instead of streaming. The exact coalesced scan assertion now compares every ordered key/value tuple. Snapshot pin diagnostics count the unique SSTs retained by live transaction and scan snapshots immediately, rather than waiting for those files to become obsolete.

The bounded qualification includes local/simulated-cloud effective settings, native metadata reads, shared reader lifetime and uncertain close, cancellation while another native open is held, two simultaneous scans, range masking, cold forward/reverse limited scans across forty actual SSTs and reopen, reader-cache/corruption/compaction controls, and spill cleanup. It does not qualify all platforms, all provider schedules, a complete engine memory cap, or performance parity.

A private native Midge producer also wrote its maximum public three-byte-key transaction payload of 67,108,806 bytes, synced and flushed at a 1 GiB budget, then reopened at 8 MiB. Point reads and full/limited forward/reverse scans succeeded with a 1,468,007-byte total read allowance. Pants consumed a copy of those unchanged native files through the same public read/scan paths. The permanent Pants control uses a commonly admitted near-maximum payload and the same small-budget reopen. Native producer source, hashes, and execution logs remain external qualification artifacts; no large generated database fixture is checked into this repository.

## Adjacent promotion qualification

Exact20% compaction sizing exposed a64KiB local/default-background schedule that refused compaction through Pants `FlushAsync`, while the native counterpart acknowledged64 rows and promoted its disjoint SSTs without physical compaction. The separate [promotion slice](MidgeBehaviorSstPromotion.md) now qualifies that schedule through eight unchanged SST identities and a successful manual drain. It avoids reader hydration and physical output admission for eligible moves.

The general small-memory progress control uses2MiB. Explicit physical-work controls still retain typed refusal at64KiB local/512KiB simulated cloud, then reopen at8MiB and check every acknowledged row plus a fresh durable commit. Cold runtime point/scan metadata admission remains distinct from metadata-only promotion. The shared maintenance pool and publication envelope are not enlarged to hide either boundary.
