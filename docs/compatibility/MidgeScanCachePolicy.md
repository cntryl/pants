# Scan cache insertion policy

Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf` chooses normal cache insertion
for an explicitly limited scan of at most 64 rows. Larger and unbounded scans
bypass insertion. Both policies consult blocks already cached. The reference
decisions are `engine/api/query.rs` and `sst/fs/reader_io/index.rs`.

Pants now exposes nullable `PantsScanQuery.CacheFill` with `Normal` and `Bypass`.
An explicit setting overrides the limit-based default. Immutable scan bounds
carry that decision through the local/provider reader pipeline. Cache keys use
the complete manifest SST identity, including its content checksum.

Scans borrow immutable cached bytes. A miss is inserted only after complete
entry decoding, index/boundary validation and successful decoded-buffer
admission. Scan disposal records its actual block cache hits/misses and keeps
existing cursor/resource cleanup behavior. Compaction does not acquire the
public scan insertion policy.

Four real-engine failures proved the previous limit-1/64 mismatch in Local and
simulated cloud; four limit-65/unbounded controls already passed. The new suite
contains 31 policy cases, including reverse/explicit overrides, existing hits
under bypass, invalid-policy cleanup and provider range reads after removing the
local SST cache. The expanded gate passes 47 cases; related cache, reader, scan,
authority and unsigned-history checks pass 153 cases. Release compilation has
zero warnings/errors and scoped formatting verification passes.

Frozen before/after source, regression inputs, TRX files and hashes are retained
in `/tmp/pants-behavior-scan-cache-results-20261104`. Provider coverage uses the
real Azure provider implementation over a controlled HTTP transport; it is not
live Azure qualification. This slice does not implement Midge's directional
256 KiB read-ahead window or establish comparative performance. Those remain in
the later performance phase.
