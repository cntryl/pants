using System.Globalization;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     Generates each cycle's operations and kill point from the seed. The child and the verifier
///     both call it, so the ledger only has to carry operation ids and outcomes.
/// </summary>
/// <remarks>
///     Each lane owns a disjoint key range and runs its operations one after another, while lanes
///     run concurrently. A crash therefore leaves at most one operation per lane in flight, and
///     every key's history is totally ordered by its lane.
/// </remarks>
static class CrashSoakWorkload
{
    const string NeverWrittenValue = "crash-soak:never-written";

    /// <summary>Failpoints on the commit path, hit about once per commit.</summary>
    static readonly Failpoint[] CommitPathFailpoints =
    [
        Failpoint.BeforeWalAppend,
        Failpoint.MidWalAppend,
        Failpoint.AfterWalAppend,
        Failpoint.BeforeWalFlush,
        Failpoint.AfterWalFlush,
        Failpoint.BeforeCoalescedWalDurabilityBoundary,
        Failpoint.AfterCoalescedWalDurabilityBoundary,
        Failpoint.BeforeCoalescedCommitApply
    ];

    /// <summary>Failpoints on flush, compaction, manifest, intent-log, and WAL-rotation paths.</summary>
    static readonly Failpoint[] MaintenanceFailpoints =
    [
        Failpoint.BeforeWalRotation,
        Failpoint.AfterWalSealRename,
        Failpoint.BeforeWalSealDirectorySync,
        Failpoint.AfterWalRotation,
        Failpoint.BeforeFlushBuild,
        Failpoint.AfterFlushOutputDurable,
        Failpoint.BeforeFlushPublication,
        Failpoint.BeforeFlushDirectorySync,
        Failpoint.AfterFlushFinalizationBeforeIntent,
        Failpoint.BeforeFlushManifestPublish,
        Failpoint.AfterFlushManifestPublish,
        Failpoint.AfterCompactionOutputDurable,
        Failpoint.BeforeCompactionDirectorySync,
        Failpoint.BeforeCompactionManifestPublish,
        Failpoint.AfterCompactionManifestPublish,
        Failpoint.AfterCompactionObsoleteFilesRetired,
        Failpoint.BeforeManifestJournalAppend,
        Failpoint.AfterManifestJournalAppend,
        Failpoint.BeforeManifestJournalSync,
        Failpoint.AfterManifestJournalSync,
        Failpoint.BeforeManifestCheckpointReplace,
        Failpoint.AfterManifestCheckpointReplace,
        Failpoint.BeforeIntentLogReplace,
        Failpoint.AfterIntentLogReplace
    ];

    /// <summary>Failpoints on cloud WAL, catalog, and SST publication paths.</summary>
    static readonly Failpoint[] CloudFailpoints =
    [
        Failpoint.AfterCloudWalSealFlush,
        Failpoint.BeforeCloudWalUpload,
        Failpoint.AfterCloudWalUpload,
        Failpoint.BeforeCloudCatalogPublish,
        Failpoint.AfterCloudCatalogPublish,
        Failpoint.BeforeCloudUpload,
        Failpoint.AfterCloudUpload
    ];

    public static string NeverWritten => NeverWrittenValue;

    public static CrashSoakCycle CreateCycle(CrashSoakScenario scenario, int cycle)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var random = new Random(unchecked((scenario.Seed * 1_000_003) + (cycle * 7_919) + 17));
        var nextId = 0;
        var lanes = new List<IReadOnlyList<CrashSoakOperation>>(scenario.LaneCount);
        var perLane = Math.Max(4, scenario.OperationsPerCycle / scenario.LaneCount);
        for (var lane = 0; lane < scenario.LaneCount; lane++)
        {
            var generator = new LaneGenerator(scenario, cycle, lane, random);
            var operations = new List<CrashSoakOperation>();
            var count = perLane + random.Next(perLane / 2 + 1);
            while (operations.Count < count)
            {
                generator.Append(operations, ref nextId);
            }

            lanes.Add(operations);
        }

        var commits = lanes.Sum(static lane => lane.Count(static operation => !operation.IsMaintenance));
        return new CrashSoakCycle(cycle, lanes, CreateCrashPlan(scenario, random, commits));
    }

    static CrashSoakCrashPlan CreateCrashPlan(CrashSoakScenario scenario, Random random, int commits)
    {
        var loseLocalCache = scenario.Backend != CrashSoakBackend.Local && random.Next(4) == 0;
        var roll = random.Next(100);
        if (roll < 10)
        {
            return new CrashSoakCrashPlan(CrashSoakCrashKind.EndOfWorkload, null, 0, loseLocalCache);
        }

        if (roll < 35)
        {
            return new CrashSoakCrashPlan(
                CrashSoakCrashKind.AfterDispatch,
                null,
                1 + random.Next(Math.Max(1, commits)),
                loseLocalCache);
        }

        var family = roll < 65 || (roll >= 85 && scenario.Backend == CrashSoakBackend.Local)
            ? CommitPathFailpoints
            : roll < 85
                ? MaintenanceFailpoints
                : CloudFailpoints;
        var failpoint = family[random.Next(family.Length)];
        var maximumHit = family == CommitPathFailpoints ? Math.Max(1, commits) : 4;
        return new CrashSoakCrashPlan(
            CrashSoakCrashKind.Failpoint,
            failpoint,
            1 + random.Next(maximumHit),
            loseLocalCache);
    }

    sealed class LaneGenerator(CrashSoakScenario scenario, int cycle, int lane, Random random)
    {
        public void Append(List<CrashSoakOperation> operations, ref int nextId)
        {
            var roll = random.Next(100);
            if (lane == 0 && roll < 4)
            {
                operations.Add(Maintenance(nextId++, CrashSoakOperationKind.Flush));
                return;
            }

            if (lane == 0 && roll < 6)
            {
                operations.Add(Maintenance(nextId++, CrashSoakOperationKind.Compact));
                return;
            }

            if (roll < 70)
            {
                var id = nextId++;
                operations.Add(Operation(id, CrashSoakOperationKind.Commit, CommitMutations(id)));
                return;
            }

            if (roll < 78)
            {
                var id = nextId++;
                operations.Add(Operation(id, CrashSoakOperationKind.Rollback, Puts(id)));
                return;
            }

            if (roll < 86)
            {
                var id = nextId++;
                operations.Add(Operation(id, CrashSoakOperationKind.AssertionFailure, Puts(id)));
                return;
            }

            var winnerId = nextId++;
            var loserId = nextId++;
            var contested = RandomKey();
            operations.Add(Operation(
                winnerId,
                CrashSoakOperationKind.ConflictWinner,
                [CrashSoakMutation.Put(contested, Value(winnerId, 0))]));
            operations.Add(Operation(
                loserId,
                CrashSoakOperationKind.ConflictLoser,
                [
                    CrashSoakMutation.Put(contested, Value(loserId, 0)),
                    CrashSoakMutation.Put(RandomKey(), Value(loserId, 1))
                ]));
        }

        CrashSoakOperation Maintenance(int id, CrashSoakOperationKind kind) =>
            new(id, lane, kind, PantsDurability.Sync, []);

        CrashSoakOperation Operation(
            int id,
            CrashSoakOperationKind kind,
            IReadOnlyList<CrashSoakMutation> mutations) =>
            new(id, lane, kind, NextDurability(), mutations);

        PantsDurability NextDurability()
        {
            var roll = random.Next(100);
            return scenario.Backend == CrashSoakBackend.Local
                ? roll switch
                {
                    < 55 => PantsDurability.Sync,
                    < 80 => PantsDurability.Buffered,
                    _ => PantsDurability.BestEffort
                }
                : roll switch
                {
                    < 45 => PantsDurability.CloudStrict,
                    < 85 => PantsDurability.CloudAsync,
                    _ => PantsDurability.BestEffort
                };
        }

        List<CrashSoakMutation> CommitMutations(int id)
        {
            var count = 1 + random.Next(4);
            var mutations = new List<CrashSoakMutation>(count);
            for (var index = 0; index < count; index++)
            {
                var roll = random.Next(100);
                mutations.Add(roll switch
                {
                    < 55 => CrashSoakMutation.Put(RandomKey(), Value(id, index)),
                    < 67 => CrashSoakMutation.Insert(InsertedKey(id, index), Value(id, index)),
                    < 87 => CrashSoakMutation.Delete(RandomKey()),
                    _ => RandomRange()
                });
            }

            return mutations;
        }

        List<CrashSoakMutation> Puts(int id)
        {
            var count = 1 + random.Next(3);
            var mutations = new List<CrashSoakMutation>(count);
            for (var index = 0; index < count; index++)
            {
                mutations.Add(CrashSoakMutation.Put(RandomKey(), Value(id, index)));
            }

            return mutations;
        }

        CrashSoakMutation RandomRange()
        {
            var start = random.Next(scenario.KeysPerLane);
            var end = Math.Min(scenario.KeysPerLane, start + 1 + random.Next(6));
            return CrashSoakMutation.DeleteRange(
                CrashSoakScenario.KeyFor(lane, start),
                CrashSoakScenario.KeyFor(lane, end));
        }

        string RandomKey() => CrashSoakScenario.KeyFor(lane, random.Next(scenario.KeysPerLane));

        // Inserted keys sort before the lane's fixed keys ('i' < 'k'), so no generated range covers them.
        string InsertedKey(int id, int index) =>
            string.Create(CultureInfo.InvariantCulture, $"l{lane}/i{cycle:000}.{id:00000}.{index}");

        string Value(int id, int index)
        {
            var padding = random.Next(8) == 0 ? 1_024 + random.Next(6_144) : random.Next(48);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"s{scenario.Seed}.c{cycle}.o{id}.m{index}.") + new string((char)('a' + (id % 26)), padding);
        }
    }
}
