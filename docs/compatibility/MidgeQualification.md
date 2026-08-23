# Midge compatibility qualification

Pants is qualified against Midge commit
`c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33`. Normal builds do not clone or
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
