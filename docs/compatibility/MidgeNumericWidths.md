# Unsigned FORMAT 9 values

The reference is current Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf`
and lsm-spec `2376ba9ddc7a9dc55d5988babc3847089ea8d3e1`. Persisted sequence
and expiry values use unsigned 64-bit integers.

## Current producer evidence

A real Midge codec emitted sequence `9223372036854775824`; its public engine
then opened that history, read the row, committed at `9223372036854775827`,
and reopened both WAL-only and flushed-SST variants. Pants originally rejected
both images at checked signed conversions. The unchanged persisted writer bytes
are now inputs to `CurrentMidgeUnsignedSequenceTests`, with per-file provenance
in [the fixture manifest](MidgeUnsignedSequenceFixtures.json). Independent public
Midge reads report allocation/local-durable cut `9223372036857921552`,
last-synced cut zero and retained snapshot cut `9223372036854775827` for both
images. Pants checks these separately, then checks its two-operation commit at
allocation floor plus four.

A separate actual Pants writer extended both histories with a Sync commit at
`9223372036857921555`, preserving the originals. Current Midge opens both the
Pants WAL and flushed-SST variants, reads all three exact rows, and reports the
new applied snapshot cut. Source, compiled hashes, unchanged producer images
and results are retained in
`/tmp/pants-unsigned-sequence-writer-20261104/qualification.json`.

Runtime MVCC cuts, retained cells/range tombstones, transaction begin cuts,
conflict checks, compaction horizons, WAL requests/receipts and durability
frontiers now retain `ulong` throughout. The public API changes are breaking:

- `PantsRuntimeMetrics`: `CurrentSequence`, `ManifestLastPersistedSequence`,
  `ManifestNextWalSequence`, `WalLastSyncedSequence`, `WalLocalDurableSequence`,
  `WalCloudDurableSequence` and `WalCurrentSegmentId` are `ulong`.
- `PantsStorageLayout` manifest sequence fields and
  `PantsStorageFileLayout` nullable sequence bounds are unsigned.
- `PantsSnapshotPin.Sequence` is `ulong`; `SnapshotId` stays signed.
- `PantsStorageVerificationReport.WalBoundary` is `ulong?`, and its
  `ManifestEpoch` checkpoint identity is `ulong`.

Transaction/request/snapshot identities, byte counts and durations remain
signed. Consumers should retain unsigned sequence values rather than cast them
back to `long`. This does not change FORMAT 9, WAL bytes or SST V5.

Midge's live allocation ceiling and epoch-opening headroom are separate rules.
A transaction in either memory or disk mode consumes its operations plus begin
and commit boundaries. `UInt64.MaxValue - 1` is the final live sequence;
`UInt64.MaxValue` remains the dropped-family flush-frontier sentinel and a valid
read upper bound. Sequence exhaustion returns `ResourceLimit` before a rejected
transaction changes its sequence or WAL history.

Every writer epoch starts above the previous durable reservation and admits a
whole 1 MiB sequence block using checked arithmetic. The block may end exactly
at `UInt64.MaxValue`; creating it does not move the current cut to the new end.
Snapshot/read cuts remain the recovered applied prefix rather than that allocation
floor. Diagnostics report the allocation floor, matching Midge's separate
`sequence` and `applied_sequence` values.
A later open whose existing floor cannot add that block fails with typed
`ResourceLimit`, matching actual Midge probes. An otherwise valid exhausted
history remains available to offline verification. There is no saturation or
wrap at writer admission.

The focused unsigned gate passes 15 cases: both real Midge histories extended,
flushed, compacted and reopened with exact rows; retained snapshots and four
point/range conflict schedules; a dropped-family sentinel reopen; memory's last
live commit and rejected successor; exact epoch ceiling/admission failures;
and offline verification. Rejected epoch opens preserve authoritative WAL files
and LEDGER bytes. This is local macOS arm64 evidence; full qualification is
recorded separately from this focused gate.

The bounded width review did not qualify every pre-existing reservation-I/O
fault schedule or a recovered data-bearing tail whose writer epoch was manually
kept unchanged. Covered-reservation refill failure tolerance and same-epoch
sealing policy are separate behavior questions, not closed by changing types.

## TTL clock and calendar range

An actual public Midge writer persisted expiry `UInt64.MaxValue`. Pants kept the
row immediately below `DateTimeOffset.MaxValue` but incorrectly hid it at that
exact clock value, because clock conversion replaced it with `UInt64.MaxValue`.
Clock conversion now retains its actual Unix milliseconds. The raw expiry stays
unsigned and remains distinct from a saturated display projection.

Pants also saturated TTL arithmetic when the calendar representation overflowed,
even though the unsigned millisecond result still fit. Addition now follows
Midge `common/time.rs::expiration_from_ttl`: whole seconds times 1,000 followed
by unsigned saturating addition. A clock at `253402300799499` plus one second
persists `253402300800499`. Current Midge reads that actual Pants-written SST as
visible at `253402300800498`, then expired at `253402300800499`, both beyond the
.NET calendar representation.

`CurrentMidgeMaximumTtlWriterTests` consumes unchanged Midge output, extends it,
flushes, compacts, scans and reopens it at both clock endpoints. Resident/spilled
and flush/compaction tests now preserve future unsigned expirations. Three
failing cases and one positive control preceded the fix; the final TTL, codec,
operation-source and timestamp filter passes 89 cases. The independent writer/
consumer evidence and compiled hashes are in
`/tmp/pants-ttl-writer-cross-read-20261104/qualification.json`.
