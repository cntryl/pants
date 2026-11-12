# Current Midge behavior discovery

On 2026-11-04, Cargo compiled and listed Midge
`c684310d9d8ce8ecd1151c47151d4bea98b348cf` on macOS arm64 using locked,
offline dependencies and an isolated target directory.

| Harness selection | Discovered cases |
| --- | ---: |
| Library, all features | 3,744 |
| Ordinary integration targets, all features | 710 |
| Binary, all features | 7 |
| Rustdoc, all features | 12 |
| Explicit fault-injection target, failpoints feature | 216 |
| Total in this feature matrix | 4,689 |

```sh
cargo test --locked --offline --all-features -- --list
cargo test --locked --offline --features failpoints --test fault_injection -- --list
```

The main harness identities exactly match the current source extraction, with no
missing or extra identities. The explicit fault selection contains 216 of the
218 all-feature fault declarations. Its two excluded cases require the additional
`sqrzl-tests` feature: predecessor audit-child abort and successor strict
commit preservation after the predecessor reopens its old cache. Rustdoc is
counted once. Test discovery does not execute the scenarios.

A separate native execution passed all 435 cases in `engine_api`, `transactions`,
`column_families`, `lifecycle`, `lease_control`, `observability`,
`write_backpressure` and `cf_drop_pressure`, with all features and the same
isolated target directory. This establishes those reference outcomes on this
host; it does not certify equivalent Pants assertions or execute the full
4,689-case inventory. Exact target totals and the log digest are retained in
`/tmp/pants-midge-behavior-inventory-20261104/native-public-execution.json`.

The retained local discovery artifact is
`/tmp/pants-midge-behavior-inventory-20261104/compiled-current-discovery.json`.
It records exact identities, feature selections, reference commit, native binary
hashes and both command-log hashes. This is local evidence; the temporary artifact
is not distributed with the package.

The historical [per-case mapping](MidgeTestParityInventory.md) remains a historical
record. Compiled discovery does not promote its old mappings to current parity
evidence. Current implemented contracts and executed Pants gates are tracked in
[behavior alignment](MidgeBehaviorAlignment.md), with correctness and performance
qualification kept distinct.
