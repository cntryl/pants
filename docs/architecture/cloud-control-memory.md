# Cloud control metadata memory

WAL catalog reads use object metadata before downloading catalog contents. Pants reads the primary and mirror sizes, reserves their combined parse workspace from the shared maintenance memory pool, and only then fetches the bytes in ranges no larger than 64 KiB. A store that does not support ranges may use a whole-object read only when HEAD reports at most 64 KiB; larger objects fail with `ResourceLimit`. The decoded catalog stays charged until the caller has finished using it.

If the catalog copies do not fit the available maintenance budget, the read fails with `ResourceLimit` before any range body is allocated or catalog repair is attempted. Each range must report the identity observed by HEAD, and the response must contain the full requested range. A replacement or incomplete range aborts the read without changing catalog authority.

The catalog workspace is conservatively charged at 16 times the combined encoded size. The reservation covers the range buffers, encoded snapshots, and decoded catalog representation while concurrent maintenance activity shares the same pool. Background maintenance enters the shared gate before reading. Foreground WAL publication reserves from the same pool without waiting behind a stalled maintenance provider read. The maintenance gate is released after parsing so a WAL retirement turn can continue to its own bounded reads; the memory reservation remains live until the caller disposes the catalog read.

WAL batch publication, catalog epoch fencing, and WAL retirement count the serialized catalog with a workspace capped at 64 KiB and sized to at least 256 bytes and at most one quarter of the maintenance pool. They then reserve `4 × serialized bytes` plus a fixed workspace capped at 256 KiB and half the pool before allocating the outgoing byte array. The reservation remains live through the primary write, readback, and mirror convergence. If the combined read and write workspace does not fit, Pants returns `ResourceLimit` before changing catalog authority; uploaded epoch-scoped WAL segments remain unreferenced.

Catalog input, parse memory, writer-owned publication, and repair writes performed while reading a torn or divergent catalog are bounded by the shared pool. Repair reuses the validated catalog bytes and reserves the same publication envelope before changing either copy.

## 512 MiB production profile

Set an explicit 512 MiB runtime memory budget for a reproducible production profile:

```csharp
var options = PantsOpenOptions.Cloud(cachePath, location)
    .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
    .WithWorkloadProfile(PantsWorkloadProfile.Mixed)
    .WithMemoryBudget(PantsMemoryBudget.FromBytes(512L * 1024 * 1024));
```

This profile combines throughput goals and a mixed workload with the explicit budget. The
dependency-injection equivalent is `"MemoryBudgetBytes": 536870912`,
`"PerformanceGoal": "Throughput"`, and `"WorkloadProfile": "Mixed"`. Use
`PantsOpenOptionsResolver.Resolve(options)` to record the effective pool capacities for the selected
performance goal and workload profile. A successful cold provider open establishes readiness;
`PantsRuntimeMetrics` reports the configured total budget plus the shared maintenance pool's current,
peak, and capacity bytes. The maintenance high-water mark includes catalog reads and repairs, WAL
retirement, SST publication, and compaction because those operations share the same pool. The older
`CompactionBuffer*Bytes` values remain available for compatibility.

The maintenance peak is an admitted-resource high-water mark for that pool. It is not a process RSS
measurement and does not include CLR overhead or resources outside the explicit Pants pools.

Cloud startup reads each control metadata object's HEAD before fetching its bytes. Before allocating
a destination buffer, it reserves workspace equal to 16 times the encoded size plus a fixed parse
allowance from the same maintenance pool. Built-in stores use identity-pinned ranges no larger than
64 KiB;
stores without ranged reads may use a whole-object GET only when the object is at most 64 KiB.
Larger objects, or concurrent reads that cannot fit the shared pool, fail with `ResourceLimit`
before the corresponding body is read. Successful reservations stay live while the metadata is
decoded and its manifest SST references are checked, then release before WAL catalog admission.
Cancellation, provider failures, identity changes, incomplete ranges, and decode failures release
the temporary reservations without repairing catalog authority.
