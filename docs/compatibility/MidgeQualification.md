# Midge compatibility qualification

Pants is qualified against Midge commit
`75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`. Normal builds do not clone or
compile Midge. They use the committed contract manifest and fixtures under
`test/Pants.Tests/Fixtures/Compatibility`.

## Baseline maintenance

The former in-repository compatibility harness and Rust driver have been
removed. The pinned manifest, metadata, lock hash, and fixtures remain the
executable compatibility baseline consumed by `Pants.Tests`. Updating the
Midge revision now requires an explicitly reviewed external regeneration of
those committed artifacts. Commit the manifest, fixture metadata, and generated
artifacts together.

The current-baseline review, including compatibility and scalability changes
since the previous pin, is recorded in
[`MidgeCurrentBaselineReview.md`](MidgeCurrentBaselineReview.md).

## Fixture policy

`fixture-metadata.json` records the producer, SHA-256 hash, and coverage kind
for every persisted structure. Deterministic FORMAT, WAL, SST, manifest,
intent, catalog, object-key, and generated-database tree artifacts require
exact-byte coverage. Journal fsync times, lease identities and times, and DDL
operation identifiers are semantic-only because Midge generates those values
at runtime; each exception has a required rationale and is parsed by Pants
tests.

Generated healthy database fixtures must pass Pants validation. Canonical
future-format and legacy fixtures instead retain their expected compatibility
or rejection behavior. GitHub Actions runs the committed compatibility tests as
part of the normal suite.
