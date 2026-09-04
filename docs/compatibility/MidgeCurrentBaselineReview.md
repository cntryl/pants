# Current Midge baseline review

Pants feature and persisted-format compatibility is pinned to Midge commit
`75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`. The committed driver lock has
SHA-256 `1fe29024e1789245b1ca8b20274aea17573380d5e33cf8f1811b59a65f85f937`.
The pin is exact: qualification rejects a dirty checkout, a different commit,
or a different dependency lock.

## Reviewed delta

The preceding compatibility pin was `c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33`.
Every compatibility-bearing public symbol and integration contract at the new
pin was re-inventoried. The important changes in that range are:

| Midge change | Pants evidence |
| --- | --- |
| Typed provider configuration and preflight (`1f119aa`) | Typed S3, Azure, GCS, and OCI configuration contracts plus provider preflight tests. OCI is an additional Pants provider; it does not alter Midge bytes. |
| Bounded runtime replies and late-response ownership (`230a1aa`, `ae9cfd8`) | One admitted absolute response deadline, deterministic timeout tests, and late-response metrics. |
| Durable cloud progress after caller deadlines (`0070a79`) | CloudStrict WAL/DDL/flush/compaction tests prove indeterminate timeout classification, retained internal obligations, and reopen recovery. |
| Configured local lease TTL and hardened WAL recovery (`6764d9c`, `3033653`) | Lease-timing tests use an injected clock; recovery tests cover torn catalogs, partial WALs, coverage proofs, fencing, and cache loss. |
| Bounded compaction and partitioned outputs (`64ad111`) | Compaction resource-budget tests prove `Peak <= Capacity` and `Used == 0`; output SSTs partition at the configured target. |
| Indexed candidates and bounded overlap/debt (`fb0dcdb`, `b7e2051`, `2ab9636`) | Point/scan candidate selection and compaction-planning tests bound touched files and overlapping inputs. |
| Cardinality-independent LSM work (`75dcc39`) | The N/2N/4N owned-resource test grows SST partitions and bytes while retained payload and scan/compaction pools stay bounded. The 261.2-million-entry address case is a symbolic extrapolation, not a CI allocation. |

Midge's current WAL encoder may compress the outer value of a sufficiently
large transaction-batch record. Pants now accepts and emits that legal
`COMPRESSION` tag and verifies it with a focused codec regression plus a
current-Midge-generated database fixture.

## Executable closure evidence

- The machine-readable inventory contains 949 entries: 852 map to executable
  Pants tests and 97 have reviewed implementation/tooling or live-account
  qualification `n/a` rationales.
  There are no planned entries.
- Current Midge regenerates all 31 fixture artifacts. Deterministic bytes are
  compared exactly; time-, identity-, and process-dependent artifacts are
  parsed and validated under documented semantic exceptions.
- Four alternate-process scenarios run local and simulated-cloud databases in
  both producer orders. Each engine reads, extends, flushes, reopens, and
  verifies the other engine's state. Atomic transaction batches cover put,
  insert, TTL, point delete, range delete, and multiple column families.
- FORMAT v3, SST v4, WAL, manifest, intent, DDL, lease, publication-catalog,
  cloud-key, checksum, and compression bytes remain compatible. No persisted
  format revision was introduced.
- Scalability is closed by the disk-resident retained-memory equation and
  deterministic ownership/resource proofs in
  [`disk-resident-scale-ladder.md`](../performance/disk-resident-scale-ladder.md).
  Large scale-ladder runs remain optional operational qualification.

The older `c5ffc2d` benchmark reader is retained only for reproducible
like-for-like historical performance artifacts. It is not the current feature
or compatibility claim.
