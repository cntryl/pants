# Manifest Recovery

`manifest.snapshot.json` is the current manifest checkpoint. `manifest.journal`
contains edits after that checkpoint. `manifest.json` is a compatibility mirror
and may lag if the process stops after publishing a checkpoint and truncating the
journal but before replacing the mirror.

The `FORMAT` marker must match its canonical bytes exactly. When the marker is
absent, current or legacy manifest and intent files, or entries inside `wal/`
or `sst/`, indicate persisted state and prevent fresh initialization. Empty
`wal/` and `sst/` directories and unrelated root metadata do not count as state.

Journal envelopes carry durable edit IDs. Older bare edits have no ID, so replay
assigns them IDs in journal order from the maximum explicit or synthesized ID
seen in that journal. This maximum is independent of the snapshot checkpoint:
a legacy edit whose synthesized ID is at or below the checkpoint is already
included in the snapshot and is skipped.

Recovery may repair an incomplete final record or a zero-filled final tail only
when no later complete, checksum-valid journal frame follows it. A zero-filled
region or overlong record before a later frame is corruption; recovery preserves
the journal bytes instead of truncating edits hidden behind the damaged frame.

Strict recovery requires a valid current snapshot when one exists. A corrupt
snapshot fails open; Strict does not silently trust the compatibility mirror or
infer ownership from SST filenames.

Salvage recovery may use a valid compatibility mirror after the current snapshot
fails. This fallback always marks the database `SalvageMode`. Because the mirror
may be stale, SSTs absent from the selected manifest are retained on disk and
reported as obsolete files instead of being deleted during startup. If neither
manifest copy can be read, salvage reconstructs metadata from SSTs it can
validate and retains unowned SSTs. A salvage result is explicitly best effort
and does not claim that every committed metadata transition was recovered.

When no current snapshot exists, a valid compatibility manifest remains a
supported recovery source. In that case the manifest journal is replayed from
its recorded checkpoint as usual.
