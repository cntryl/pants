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

WAL frames and manifest-journal records use positional, vectored writes through
`System.IO.RandomAccess`. SST point-read bytes are loaded through positional
reads rather than a shared mutable `FileStream.Position`. These primitives
avoid cursor contention and keep offsets explicit at persistence boundaries.
