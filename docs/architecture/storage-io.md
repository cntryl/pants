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
