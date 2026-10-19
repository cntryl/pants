# Cloud control metadata memory

WAL catalog reads use object metadata before downloading catalog contents. Pants reads the primary and mirror sizes, reserves their combined parse workspace from the shared maintenance memory pool, and only then fetches the bytes in ranges no larger than 64 KiB. A store that does not support ranges may use a whole-object read only when HEAD reports at most 64 KiB; larger objects fail with `ResourceLimit`. The decoded catalog stays charged until the caller has finished using it.

If the catalog copies do not fit the available maintenance budget, the read fails with `ResourceLimit` before any range body is allocated or catalog repair is attempted. Each range must report the identity observed by HEAD, and the response must contain the full requested range. A replacement or incomplete range aborts the read without changing catalog authority.

The catalog workspace is conservatively charged at 16 times the combined encoded size. The reservation covers the range buffers, encoded snapshots, and decoded catalog representation while concurrent maintenance activity shares the same pool. Background maintenance enters the shared gate before reading. Foreground WAL publication reserves from the same pool without waiting behind a stalled maintenance provider read. The maintenance gate is released after parsing so a WAL retirement turn can continue to its own bounded reads; the memory reservation remains live until the caller disposes the catalog read.

This bounds catalog input and parse memory. Catalog serialization and publication have separate admission requirements and remain part of the broader cloud authority resource envelope.
