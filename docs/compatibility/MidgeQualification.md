# Midge compatibility qualification

Pants is qualified against Midge commit
`75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`. Normal builds do not clone or
compile Midge. Tests exercise Pants against the committed fixtures under
`test/Pants.Tests/Fixtures/Compatibility`; the contract manifest is a review inventory.

## Baseline maintenance

The former in-repository compatibility harness and Rust driver have been
removed. The fixtures remain the executable compatibility baseline consumed by
`Pants.Tests`. The pinned manifest, metadata, and lock hash retain provenance for
review, not automated integrity gates. Updating the Midge revision requires an
explicitly reviewed external regeneration of those committed artifacts. Commit
the manifest, fixture metadata, and generated artifacts together.

The current-baseline review, including compatibility and scalability changes
since the previous pin, is recorded in
[`MidgeCurrentBaselineReview.md`](MidgeCurrentBaselineReview.md).

## Fixture policy

`fixture-metadata.json` records the producer, SHA-256 hash, and coverage kind
for persisted structures. These records describe fixture provenance; tests do
not validate metadata schemas, recorded hashes, or inventory status strings.
Compatibility tests exercise Pants codecs, lease handling, recovery, and offline
verification against the fixtures. Byte comparisons cover persisted-format behavior
or prove that a read-only operation leaves storage unchanged. Journal fsync times,
lease identities and times, and DDL operation identifiers vary at runtime and are
compared semantically where relevant.

Generated healthy database fixtures must pass Pants validation. Canonical
future-format and legacy fixtures instead retain their expected compatibility
or rejection behavior. GitHub Actions runs the committed compatibility tests as
part of the normal suite.
