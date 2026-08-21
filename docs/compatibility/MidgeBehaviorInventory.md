# Midge contract inventory

Pants is baselined against Midge commit
`c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33`.

The committed, machine-readable inventory is
[`MidgeContractManifest.json`](../../test/Pants.Tests/MidgeContractManifest.json).
It records each source symbol or integration test, its observable behavior, the
expected Midge error when statically discoverable, the mapped Pants test, and
coverage status. Public exports and public integration tests are canonical;
private Rust-only tests do not override them.

The inventory is intentionally static for now. Baseline refresh and executable
compatibility tooling can be introduced later as a separately reviewed change.
