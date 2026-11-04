# LSM specification FORMAT 9

The current target is `cntryl/lsm-spec` revision
`2376ba9ddc7a9dc55d5988babc3847089ea8d3e1` (FORMAT 9 amendment), checked against Midge revision
`c684310d9d8ce8ecd1151c47151d4bea98b348cf`, version 0.5.2 main.
Older compatibility reviews and fixture directories are historical evidence.
They do not identify the format emitted by current Pants writers.

Both upstream main branches were fetched on 2026-11-04. The published spec merge
has the same tree as `7748fd1`, the revision used for the committed fixture directory.
The current follow-up findings and validation are recorded in
[Midge 0.5.2 parity](Midge052Parity.md).

## Persisted contracts

| Structure | Current profile |
| --- | --- |
| Database marker | FORMAT 9; other versions rejected without rewriting |
| SST | V5 compact canonical varints, independent restart directories and validated continued-key bits |
| Manifest snapshot | Kind-1 `MDGEMD09` frame, named MessagePack map, big-endian CRC32C |
| Manifest journal | Edit envelopes and fsync markers in kind-2 frames, outer big-endian CRC32C |
| Intent log | Chained append-only kind-3 suffix edits, verified prefix repair before append |
| Local WAL | Ledger V2, linked 128-byte segment headers V2, payload V2, footer V1 |
| Cloud WAL catalog | V3, chain anchor, sealed facts, sequence floor and retirement generation |
| Cloud metadata | Layout V2; separate committed pointer and immutable per-file generation closures |
| Cloud lease / sentinel | Lease V4 and initialization sentinel V3 |
| DDL registry | V2 with `ddl_epoch` and `writer_epoch` |

Manifest retirement preserves exact removed SST identities until confirmed deletion.
`MoveSsts` changes only logical levels after validating the complete captured layout.
Fragment publication and recovery consult manifest authority together with the intent;
an intent phase does not establish commitment.

The cloud writer uses the specification's explicit immutable per-file generation
representation. The reader also accepts validated immutable version chains, including
parent links, ordinals, content proofs and metadata-cut digests. Uploads, cache contents
and LIST ordering never select committed metadata. A cache continuity record is local
policy used to restrict recovery of unpublished WAL after takeover; it is not shared
metadata authority.

Remote SST deletion is deliberately conservative after takeover: identities imported
from the predecessor's active or retired manifest remain retained because the new
writer cannot prove that predecessor readers have released them. Same-session
identities become deletion candidates only after local readers, snapshots and intent
dependencies release; confirmed conditional deletion is followed by a durable
`CompleteSstDelete` edit and metadata publication. Inherited retirement obligations
can therefore retain extra cloud objects until a reader-liveness proof is available.

This is a deliberate persisted-format break. Existing databases require logical
export/import into a fresh database or cloud prefix. There is no in-place upgrade or
binary rollback. WAL and SST version gates are checked independently of FORMAT.

## Fixture evidence

`test/Pants.Tests/Fixtures/Compatibility/LsmSpec/7748fd1` contains unchanged
published specification fixtures and schemas. Behavioral tests decode and reopen the
empty and populated FORMAT 9 fixtures. Historical FORMAT 8 schema examples remain
validated against their historical schemas; schema acceptance is distinct from
engine compatibility.

The WAL V2 segment fixtures under the Midge `c684310d...` directory were copied
unchanged from Midge's `wal-tombstone-gc-9734fab3` fixture. Those bytes were originally
re-encoded for FORMAT 6, whose WAL V2 remains compatible. They prove segment-reader
compatibility, not whole-database FORMAT 9 compatibility.

Focused local tests cover framing, recovery, corruption, retirement, level moves,
SST visibility, pointer fencing, lease parsing and provider takeover. Local passing
tests are not hosted CI, cloud-provider qualification or complete Midge behavioral
parity. The existing parity inventory remains a separate historical audit.

## Local validation — 2026-11-03

The follow-up [2026-11-04 complete validation](Midge052Parity.md#final-local-validation-2026-11-04)
includes the Sqrzl suite and passes 3,163 tests with two opt-in/live-service skips.
The results below describe the preceding migration snapshot.

- `dotnet restore --locked-mode` passed without dependency changes.
- `dotnet build Pants.slnx --configuration Release --no-restore -m:1` passed with zero warnings and errors.
- `dotnet format Pants.slnx --verify-no-changes --no-restore` passed.
- Release regression suite excluding `Sqrzl`: 3,071 passed, zero failed, two skipped.
  The skipped cases require live AWS S3 and opt-in soak configuration. Sqrzl
  provider qualification was excluded from this local run.
- `git diff --check` passed.

These results qualify the local regression suite, not hosted CI or a production release.
