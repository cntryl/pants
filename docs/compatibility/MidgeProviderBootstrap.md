# Initial provider metadata authority

The reference is Midge 0.5.2 `c684310d9d8ce8ecd1151c47151d4bea98b348cf`,
`src/engine/startup/storage.rs`. The implementation is integrated; final combined
qualification remains separate from the isolated and focused results below.

A new provider namespace must publish its first committed metadata generation
before creating local FORMAT, manifest, intent or WAL state. Local cache bytes
cannot stand in for missing shared authority. The bootstrap candidate is an
immutable bounded in-memory FORMAT 9 closure, with its own Bootstrap/MemoryOnly
accounting identity. Its actual MemoryStream staging writes are distinct from
runtime Persistent costs; they do not manufacture filesystem/device barriers.
Admission precedes allocation. The candidate and its claim stay owned through
publication, exact authority readback and hydration; hydration separately owns
its downloaded control objects.

Bootstrap requires a claimed nil metadata pointer, a pristine WAL catalog, no
DDL history, no remote SST objects and no foreign local cache history. Catalog
and DDL fences use conditional versions under the acquired writer epoch before
the first generation is published. LIST only refuses an unsafe bootstrap; it
never selects committed metadata. Confirmation reads use bounded shared-pool
admission. An existing committed generation goes through normal authoritative
hydration after releasing the preliminary probe's claims.

An immutable-generation upload failure leaves the pointer nil and creates no
local authoritative files. A lost pointer-CAS callback is reconciled through the
existing exact-candidate readback; a successor can select the committed generation
from either the original or a fresh cache. The aggregate OpenTimeout can abandon
the caller while a physical upload remains held. Cleanup keeps the writer lease
and candidate until the physical operation finishes, preventing a successor from
being admitted during that interval.

Provider and simulated opens acquire cache-path exclusion before remote effects
and transfer the same open LOCK handle into local storage. An already active
writer is rejected before remote mutation. Physical file identity is checked
after awaited authority validation and before hydration/transfer, so renaming
and replacing the configured directory cannot redirect startup writes into a
replacement writer's namespace. Only the exact owned coordination file is
permitted by the otherwise strict empty-cache proof.

`ProviderCloudBootstrapTests` first reproduced seven failures: premature local
authority materialization and acceptance of foreign local/catalog/DDL/SST history.
An additional bounded-confirmation regression reproduced a full-object catalog
read. The isolated final filter passes ten cases; the broader provider metadata,
startup and deadline filter passes 162, with a zero-warning Release solution
build and scoped formatting verification. These are named deterministic engine
schedules and in-memory provider protocols, not a native-provider deployment or
all possible effect/response orderings. Artifacts are retained under
`/tmp/pants-bootstrap-next-results`.

Subsequent actual engine regressions reproduced conflicting local/provider cache
owners and mutation of a replacement writer after directory rename/recreate.
The repaired bootstrap/exclusion slice passes 334 focused cases; a known old
backup lease fixture was excluded in that isolated worktree and is already
corrected in the canonical tree. The integrated startup, deadline, backup,
accounting-origin and deferred-recovery filter passes 57 cases. Native physical
identity is executed on macOS arm64; Linux64 and Windows require their platform
gates.
