# Midge compatibility qualification

Pants is qualified against Midge commit
`75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`. Normal builds do not clone or
compile Midge. They use the committed contract manifest and fixtures under
`test/Pants.Tests/Fixtures/Compatibility`.

## Refreshing the baseline

Use a clean sibling Midge checkout whose `HEAD` is exactly the pinned commit:

```shell
dotnet run \
  --project eng/compat/Pants.CompatibilityHarness/Pants.CompatibilityHarness.csproj \
  --configuration Release \
  -- refresh --midge ../midge
```

The refresh builds the release-only Rust driver from the committed Cargo lock,
copies Midge's canonical fixtures, emits wire and storage goldens, validates
their hashes and structure, and refreshes the manifest source-tree hash. It
stages all output before replacing the fixture tree and manifest as one
transactional publication. A Git-directory lock prevents concurrent refreshes.
A fingerprinted transaction marker rolls a process-interrupted publication
back or completes its committed cleanup on the next refresh without deleting
unrecognized post-interruption edits.

Publishing refreshes refuse to replace uncommitted fixture or manifest changes.
Review and commit those changes first. Use `refresh --force --midge ../midge`
only when the current generated baseline is intentionally being replaced.

Commit the manifest, fixture metadata, and generated artifacts together.

Use `refresh --check --midge ../midge` to regenerate into an isolated workspace
and prove that every deterministic artifact and manifest byte matches the
committed baseline. Semantic-only fixture values are regenerated and validated,
but their documented process- or time-dependent bytes are excluded from this
equality check. CI runs this mode and also enforces Rust formatting and Clippy
with and without the fixture-only failpoints feature. The disposable CI checkout
then publishes a second regeneration and runs Pants fixture tests against those
fresh semantic values.

## Running qualification

```shell
dotnet run \
  --project eng/compat/Pants.CompatibilityHarness/Pants.CompatibilityHarness.csproj \
  --configuration Release \
  -- qualify --midge ../midge
```

Qualification covers local and simulated-cloud storage in both producer
orders. Midge and Pants create, reopen, read, mutate, flush, and reopen the same
database through separate child processes. Each writer shuts down before the
next starts, so the single-writer lease is never bypassed. Both offline
verifiers run between handoffs and must leave the fixture byte-for-byte
unchanged. Qualification builds Midge without failpoints; fixture refresh alone
enables them to capture otherwise transient storage states.

Each writer commits one atomic transaction batch in the default and an
additional column family. The batch covers put, insert, TTL, point-delete, and
range-delete semantics. The create boundary remains WAL-only for both local
producers and for Pants simulated-cloud writes; current Midge eagerly publishes
CloudStrict writes to SSTs. Later boundaries force an SST flush from each
engine. Consequently the four scenarios prove both WAL and SST recovery where
the producer exposes those states, as well as manifest, catalog, lease, and
column-family compatibility. They also prove that each engine can extend the
other engine's database rather than merely parse frozen bytes.

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

Generated healthy database fixtures must pass both offline verifiers. Canonical
future-format and legacy fixtures instead retain their expected compatibility
or rejection behavior. GitHub Actions runs the full Debug and Release suites,
plus bidirectional qualification, on Linux, macOS, and Windows.
