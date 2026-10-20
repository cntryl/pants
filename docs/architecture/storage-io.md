# Storage I/O Boundaries

Pants centralizes metadata replacement in `AtomicStagedFile`. The helper writes
a uniquely named file in the target directory, flushes that file to stable
storage, invokes any injected pre-publication failure, atomically renames it
over the target, and flushes the parent directory. Temporary cleanup is
best-effort; uncertain targets are never deleted.

Unix opens and syncs the directory through its native file descriptor. Windows
opens a writable directory handle with backup semantics and calls
`FlushFileBuffers`. Both paths preserve Midge's file-sync, atomic-rename, and
parent-directory-sync durability boundary.

Local database initialization also treats directory entries as durable state:
it creates the database root and storage subdirectories one level at a time,
syncing each parent directory after creating a child. The initial `wal.log`
open is followed by a sync of the `wal` directory before open succeeds. File
content sync and parent-directory sync are separate steps; syncing `wal.log`
alone does not establish that the `wal.log` name survives a crash.

The configured local root is resolved to its filesystem target when the store
opens; the root itself may be a symbolic link. Before file access, Pants checks
each existing path component beneath that resolved root for links or reparse
points. This is a check immediately before I/O, not an operating-system
no-follow handle, so replacing a component between the check and the operation
can still race the guard.

A rename over an existing target must succeed while another process reads it,
as it does on POSIX. Windows `MoveFileEx` refuses with access denied whenever
any handle is open on the target, so that refusal falls back to a
POSIX-semantics rename (`FileRenameInfoEx` with
`FILE_RENAME_FLAG_POSIX_SEMANTICS`), as Midge does: existing readers keep the
old file and new opens see the replacement. When the fallback is unavailable
the original refusal surfaces and the transient-sharing retry still applies.
Readers of the `.midge_leader` record open it sharing read, write, and delete,
so a lease check on one process never blocks a renewal or takeover on another.
Lease I/O failures while acquiring surface as `LeaseUnavailable`.

The lease mutation-lock file is intentionally the one exception to staged
replacement. Its atomic `CreateNew` at the final path is the mutual-exclusion
primitive itself; publishing it by rename would allow multiple contenders to
prepare ownership simultaneously. SST flush staging and WAL rotation likewise
retain their recovery-specific immutable naming protocols rather than using a
metadata replacement helper.

The mutation lock is never broken automatically: a plain open refuses with
`LeaseUnavailable` while `.midge_leader.lock` is present, whatever its age. A
writer killed while holding it leaves it behind. Recover explicitly, with no
writer running against the database, through
`PantsDatabase.RecoverStaleLeaseMutationLockAsync(path, leaseConfiguration)`.
Pass the same `PantsLeaseConfiguration` as the writers. The call returns `true`
when it removed the lock and `false` when none existed. It removes the lock only
when the `.midge_leader` record is absent or older than the TTL plus clock skew,
and only when the lock still carries the owner token it read first. It throws
`LeaseHeld` while the record is still live or another process still holds the
lock open, and `LeaseIndeterminate` when the
lock has no owner token or the record cannot be proven stale.

WAL frames and manifest-journal records use positional, vectored writes through
`System.IO.RandomAccess`. SST point-read bytes are loaded through positional
reads rather than a shared mutable `FileStream.Position`. These primitives
avoid cursor contention and keep offsets explicit at persistence boundaries.

## WAL Transaction Admission and Recovery

WAL-backed transactions are checked against the replay working-set limit before
sequence reservation or the first WAL write. Resident and spilled operation
sources use the same estimator; spilled transactions are checked as operations
are visited, without building a second full mutation array. Recovery applies a
committed transaction whole and publishes checkpoints when recovered memory
reaches its target.

In hybrid mode, current local-cache headroom lowers the checkpoint target, but
it does not reject an otherwise recoverable transaction. Checkpoint staging is
charged to the local-storage ledger and may proceed past that soft cache budget,
because recovery must publish to release its working set; later hybrid
maintenance can evict older cloud-durable SSTs. The recovery spool for split
transactions remains budgeted separately and fails with a resource error if its
bytes cannot be reserved. Neither resource failure is treated as WAL corruption
or salvaged by truncating acknowledged records.

Hybrid WAL appends reserve their local frame bytes against the same ledger before
writing the first frame. Admission includes resident WAL and SST bytes plus
outstanding flush, compaction, hydration, and spill reservations, and applies the
98% emergency ceiling used for user writes. A single transaction and a coalesced
group are each admitted as one append; spilled transactions reserve the
worst-case size of all begin, mutation, and commit frames before writing any of
them. A capacity rejection leaves the WAL untouched and the writer usable.

An append reservation stays charged through its durability boundary. If an
append fails, Pants releases the reservation only after truncating the WAL to
its previous length and flushing that rollback. If rollback cannot be proven,
the writer is fenced and the reservation remains charged until the runtime is
discarded; on reopen, the ledger measures any retained WAL bytes from storage.

## Hybrid Flush Headroom

Hybrid writes also reserve their future SST output before the WAL is touched.
Admission computes a conservative V4 SST bound for each affected column family,
including data blocks, Bloom filters, sparse and trie indexes, range tombstones,
compression expansion, block trailers, metadata, and the footer. The combined
bound across a transaction's families must fit within half of the configured
local budget. An oversized transaction is rejected before append, including
when its operations are spilled.

Cloud mode also flushes when aggregate active memtable bytes reach one quarter
of the local budget. This pressure signal spans all column families, so many
small families make progress before the shared budget approaches its hard cap.

Each family output claim stays charged while its operations remain in the
mutable memtable. Freezing transfers the claims to that flush generation; a
successful manifest publication releases them, after which the resident SST is
counted by the ledger. WAL, spill, hydration, and compaction admission all see
these claims, so they cannot consume space already promised to a flush.
Cloud-durable SST eviction is planned against the admission operation's actual
ceiling and is performed only when removing the full planned set makes the
request fit.

The ledger also charges files left behind when startup residue cleanup cannot
remove them. This includes retained files under `cloud_recovery` and
`sst/.flush-staging`, temporary files at the database root or directly under
`sst`, and unowned top-level SSTs through the normal SST accounting. Retained
file lengths are checked during admission and stop counting once those files
are deleted.

Startup recovery remains a separate path. Each recovery checkpoint reserves the
sum of `SstFlushFootprint` upper bounds for its per-family SST outputs before
staging them. Strict local-storage mode rejects a checkpoint that does not fit.
Hybrid recovery may cross the soft local-cache budget to release replay memory,
but only while the currently charged cache is at or below that budget. If a
failed publication leaves recovery SSTs retained over budget, another
over-budget checkpoint fails with `NoSpace` until those files are removed.

The recovery checkpoint runs after a whole WAL transaction reaches its target.
The target is at most the configured memtable size; the transaction limit is
eight times that size. Therefore a checkpoint contains less than one target
plus one maximum transaction, or at most nine times the configured memtable
size in recovered mutation accounting. The worst-case local-cache overage is
bounded by the sum of per-family `SstFlushFootprint` upper bounds for that
checkpoint's operations. Cloud recovery publishes the checkpoint before
evicting its local SSTs, so a successfully published checkpoint releases that
overage before recovery proceeds to another checkpoint.

## Flush Construction Memory

Persistent flushes reserve construction scratch before copying mutable operations
or building a frozen generation's SST plan. The allowance is derived as four
times the larger of the memtable size limit and one eighth of the engine memory
budget, plus one mebibyte. `SstFlushFootprint` supplies a conservative estimate
of four times the point and range payload bytes plus 192 bytes per operation
for grouping, entries, sorting, indexes, and range metadata. Memtable accounting
includes 64 bytes per operation, so a generation at its configured limit fits
below the derived allowance; the extra mebibyte covers fixed block and
collection overhead. The single-reader flush worker releases the reservation
after construction completes.

SST key bounds are found by scanning point keys and range endpoints once. Flush
metadata no longer creates a second sorted list of every key or a list of every
sequence number.
