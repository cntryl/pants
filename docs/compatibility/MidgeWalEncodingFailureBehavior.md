# Accepted WAL encoding failures

Reference: Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf`.
This slice follows the two distinct admission boundaries in
`src/runtime/actors/wal/transaction.rs::build_transaction_batch_unit` and
`append_prepared_transaction_batches`, together with the physical frame limit in
`src/wal/frame.rs::encoded_frame_len`.

| Boundary | Observable contract |
| --- | --- |
| Decoded transaction batch exceeds 64 MiB | `ResourceLimit`, before sequence allocation and physical writer acceptance. The old applied cut and writable suffix remain usable. |
| Configured cloud replay envelope cannot hold the resident or streamed unit | `ResourceLimit` before allocation. The limit is the smaller of one sixteenth of total memory and half the configured local-storage budget. |
| Hybrid raw frame or storage-budget admission rejects before the physical encoder | A definite admission error retains the allocated sequence, releases unused reservations and leaves the writer healthy. Later writes may proceed. |
| Accepted resident batch cannot encode its outer physical frame | The first call returns its definite `InvalidArgument`. Its sequence remains allocated, its operations remain invisible, and the physical writer is poisoned. |
| Later mutation after encoding poison | `RecoveryFailed`, including `BestEffort`. This differs from Pants' existing ambiguous-I/O fence, which still permits memory-only BestEffort work. |
| Read-only and pure empty Buffered/BestEffort completion | Return the retained applied/start cut without allocating a sequence or changing WAL bytes. |
| Valid assertion-only Buffered/BestEffort completion | Validate against the old applied cut and return its sequence. Assertions require no physical WAL writer. |
| Assertion-only or empty Sync completion | Validate preconditions first, then report `RecoveryFailed` at the required physical durability boundary. |
| Shutdown after encoding poison | Join existing physical workers and release storage/lease ownership, then return the terminal `RecoveryFailed`. Repeated explicit shutdown reports the same failure; disposal after closure is complete. |

Pants captures resident mutations once, validates their exact decoded size before the
append rollback scope, and passes the same immutable byte owners into physical encoding.
Cloud replay admission is checked after serialized preconditions, before cache capacity
housekeeping, and again against the worker's prepared immutable inputs. It counts optional
field presence without sampling the TTL clock. Midge's 64-bit resident replay envelope is
twice the nested payload plus 416 bytes per operation; streamed pending state is 128 bytes
plus 288 bytes and the key/value/range bytes per operation. These compatibility admission
bounds are separate from .NET allocation accounting. Local storage has no configured cloud
replay cap. Midge sources are `CloudReplay::limits` in
`engine/startup/streaming_recovery.rs`, `txn_batch_decoded_bound` in `wal/encoding.rs`,
and `spilled_replay_bytes` in `runtime/actors/wal/spill.rs`.

Hybrid admission measures the resident or streamed raw frame before value compression,
including its possible compression tag, and reserves the complete unit before encoding or
writing. This follows `WalActor::admit_wal_records` and `admit_spilled_wal`. Its rejection
does not poison the physical writer and does not run truncate/fsync rollback. Resident
coalescing first prepares every logical unit, then admits the group and physically encodes
its immutable inputs.

The failed physical encoder has its own `WalEncodingState`; `WalIoState` continues to
describe ambiguity after append or sync I/O. An encoder rejection does not enter physical
truncate/fsync rollback, publish memtable roots, credit WAL append metrics, or restore its
already allocated sequence. Snapshot cleanup keeps retained sources available for recovery.

Actual native probes use a three-byte key and a 67,108,806-byte value. Compressible bytes
commit; deterministic incompressible bytes pass staging and the decoded bound but fail the
outer frame. After seeding sequence 3, the failure leaves allocator sequence 6 and read-only
receipt 3. A 67,108,830-byte compressible value instead exceeds the decoded nested bound:
it returns `ResourceLimit`, stays at sequence 3, and the next Sync write returns 6. Native
source, full receipts, shutdown/reopen results, and logs are retained outside the repository
under `/tmp/pants-entry-capacity-native-receipts-20261104`.

Additional native controls are retained under
`/tmp/pants-entry-capacity-native-followups-20261104`. A 2 GiB memory budget rejects the
same resident value with `ResourceLimit` (134,218,144 required replay bytes exceed
134,217,728). At 4 GiB, simulated cloud reaches raw-frame admission and rejects both
compressible and random bytes with `InvalidArgument`: allocator 6, applied cut 3, then
healthy CloudStrict receipt 9 and BestEffort receipt 12. A genuine streamed spill has the
same admission outcome. After local encoding poison, valid assertion-only Buffered and
BestEffort transactions still return 3; assertion-only and empty Sync return `RecoveryFailed`.

`PantsWalEncodingFailureBehaviorTests` exercises these outcomes through the public engine,
checks exact old values and receipts, verifies recovery while the closed failed object is
still alive, and holds a real immutable flush output across shutdown to verify lease
retention until physical cleanup settles. Existing WAL ambiguity, apply-failure,
coalescing, lifecycle, recovery, and capacity suites remain separate controls. This slice
does not claim arbitrary encoder-memory failures or unexecuted operating-system fault
campaigns have been qualified.
