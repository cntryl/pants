# Midge compatibility qualification

Pants is qualified against Midge commit
`7d39f86217fdb07191a83bd885a514dd5ea9723f`. Normal builds do not clone or
compile Midge. Tests exercise Pants against the committed fixtures under
`test/Pants.Tests/Fixtures/Compatibility`; the contract manifest is a review inventory.

## Baseline maintenance

The former in-repository compatibility harness and Rust driver have been
removed. The fixtures remain the executable compatibility baseline consumed by
`Pants.Tests`. The pinned manifest, metadata, and lock hash retain provenance for
review, not automated integrity gates. Updating the Midge revision requires an
explicitly reviewed external regeneration of those committed artifacts. Commit
the manifest, fixture metadata, and generated artifacts together.

The removed driver remains in history at `eng/compat/MidgeDriver/pants_compat.rs`
(the parent of `ca8c5df`). A refresh, done outside this repository:

1. Clone Midge at the target commit, copy the driver to `src/bin/pants_compat.rs`,
   and run `cargo build --release --locked --no-default-features --features failpoints --bin pants_compat`
   against Midge's committed `Cargo.lock`; that lock becomes `Tooling/<sha>/Cargo.lock`.
2. Run `pants_compat emit-wire-goldens <Wire/<sha>>` and
   `pants_compat emit-storage-goldens <Storage/<sha>>`, and copy Midge's
   `tests/fixtures/compatibility` to `Midge/<sha>`.
3. Run `pants_compat local-verify` on both generated databases and confirm the
   trees are unchanged; recompute the database tree fingerprint, the artifact
   hashes in `fixture-metadata.json`, and the manifest's `sourceTreeSha256`.
4. Rebuild the driver at the previous pin with its recorded lock and diff its
   output against the committed fixtures, so that every byte difference at the
   new pin is attributable to Midge rather than to the procedure.

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
