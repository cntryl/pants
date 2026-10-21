using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class LeveledCompactionPlannerTests
{
    [Fact]
    public void ShouldPickBoundedL0InputsAndEveryOverlappingL1File()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l0-2.sst", 0, 2, "d", "f"),
            File("l0-3.sst", 0, 3, "g", "i"),
            File("l1-1.sst", 1, 4, "b", "e"),
            File("l1-2.sst", 1, 5, "e", "h"),
            File("l1-unrelated.sst", 1, 6, "x", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 3, maximumInputFiles: 5),
            42,
            false);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan.TargetLevel);
        Assert.Equal(42, plan.SnapshotHorizon);
        Assert.True(plan.PointTombstoneGcEligible);
        Assert.False(plan.RangeTombstoneGcEligible);
        Assert.Equal(
            ["l0-1.sst", "l0-2.sst", "l0-3.sst", "l1-1.sst", "l1-2.sst"],
            plan.Inputs.Select(static file => file.Name));
    }

    [Fact]
    public void ShouldShrinkSourceSetWhenOverlapClosureExceedsInputLimit()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z"),
            File("l0-2.sst", 0, 2, "a", "z"),
            File("l1-1.sst", 1, 3, "a", "f"),
            File("l1-2.sst", 1, 4, "g", "m"),
            File("l1-3.sst", 1, 5, "n", "z")
        ];

        var wide = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 2, maximumInputFiles: 4),
            null,
            false);
        var healthy = LeveledCompactionPlanner.Pick(
            [
                File("healthy-1.sst", 0, 1, "a", "b", columnFamilyId: 1),
                File("healthy-2.sst", 0, 2, "c", "d", columnFamilyId: 1)
            ],
            1,
            Configuration(l0FileCountTrigger: 2, maximumInputFiles: 4),
            null,
            false);

        Assert.NotNull(wide);
        Assert.Equal(4, wide.Inputs.Count);
        Assert.Contains(wide.Inputs, file => file.Name == "l0-1.sst");
        Assert.DoesNotContain(wide.Inputs, file => file.Name == "l0-2.sst");
        Assert.Equal(3, wide.Inputs.Count(file => file.Level == 1));
        Assert.NotNull(healthy);
        Assert.Equal(2, healthy.Inputs.Count);
    }

    [Fact]
    public void ShouldPlanSingleSourceFileWhenCompleteTargetSpanExceedsInputLimit()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z"),
            File("l0-2.sst", 0, 2, "a", "z"),
            File("l1-1.sst", 1, 3, "a", "f"),
            File("l1-2.sst", 1, 4, "g", "m"),
            File("l1-3.sst", 1, 5, "n", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 2, maximumInputFiles: 2),
            null,
            false);

        Assert.NotNull(plan);
        Assert.Single(plan.Inputs, file => file.Level == 0);
        Assert.Equal(3, plan.Inputs.Count(file => file.Level == 1));
    }

    [Fact]
    public void ShouldFragmentSourceWhenSingleSourceOverlapExceedsPreferredWorkTarget()
    {
        const ulong mib = 1024 * 1024;
        FileMeta[] files =
        [
            File("source-0.sst", 2, 1, "b", "y", 128 * mib, keyBoundsComplete: true),
            File("source-1.sst", 2, 2, "z", "zz", 128 * mib, keyBoundsComplete: true),
            File("source-2.sst", 2, 3, "zzz", "zzzz", 128 * mib, keyBoundsComplete: true),
            File("source-3.sst", 2, 4, "zzzzz", "zzzzzz", 32 * mib, keyBoundsComplete: true),
            File("target-0.sst", 3, 5, "a", "d", 128 * mib, keyBoundsComplete: true),
            File("target-1.sst", 3, 6, "e", "h", 128 * mib, keyBoundsComplete: true),
            File("target-2.sst", 3, 7, "i", "l", 128 * mib, keyBoundsComplete: true),
            File("target-3.sst", 3, 8, "m", "p", 128 * mib, keyBoundsComplete: true),
            File("target-4.sst", 3, 9, "q", "t", 128 * mib, keyBoundsComplete: true),
            File("target-5.sst", 3, 10, "u", "x", 128 * mib, keyBoundsComplete: true)
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 3, l1TargetSizeBytes: 40L * 1024 * 1024),
            42,
            false,
            512L * 1024 * 1024);

        Assert.NotNull(plan);
        Assert.Equal(CompactionPurpose.Fragment, plan.Purpose);
        Assert.Equal(2u, plan.SourceLevel);
        Assert.Equal(2u, plan.TargetLevel);
        Assert.Equal("source-0.sst", Assert.Single(plan.Inputs).Name);
        Assert.Equal(TestBytes.FromString("m"), plan.FragmentSplitKey);
        Assert.Null(plan.SnapshotHorizon);
        Assert.False(plan.PointTombstoneGcEligible);
        Assert.False(plan.RangeTombstoneGcEligible);
        Assert.True(LeveledCompactionPlanner.HasSameTargetSpan(plan, files));
        Assert.False(LeveledCompactionPlanner.HasSameTargetSpan(
            plan,
            [.. files, File("new-overlap.sst", 2, 11, "g", "h", keyBoundsComplete: true)]));
        Assert.False(LeveledCompactionPlanner.HasSameTargetSpan(
            plan with { FragmentSplitKey = TestBytes.FromString("n") },
            files));
    }

    [Fact]
    public void ShouldPrioritizeCriticalL0WorkBeforeFurtherFragmentPromotion()
    {
        FileMeta[] files =
        [
            File("source.sst", 1, 1, "a", "z", 256, keyBoundsComplete: true),
            File("target-1.sst", 2, 2, "b", "c", 256, keyBoundsComplete: true),
            File("target-2.sst", 2, 3, "f", "g", 256, keyBoundsComplete: true),
            File("target-3.sst", 2, 4, "j", "k", 256, keyBoundsComplete: true)
        ];
        var configuration = Configuration(l0FileCountTrigger: 2, l1TargetSizeBytes: 1);
        var fragment = LeveledCompactionPlanner.PickDebtDrain(
            files,
            0,
            configuration,
            null,
            preferredWorkBytes: 512);
        var l0Debt = LeveledCompactionPlanner.PickDebtDrain(
            [
                .. files,
                File("l0-1.sst", 0, 5, "l", "m"),
                File("l0-2.sst", 0, 6, "n", "o")
            ],
            0,
            configuration,
            null,
            preferredWorkBytes: 512);

        Assert.NotNull(fragment);
        Assert.Equal(CompactionPurpose.Fragment, fragment.Purpose);
        Assert.NotNull(l0Debt);
        Assert.Equal(0u, l0Debt.SourceLevel);
    }

    [Fact]
    public void ShouldKeepCompleteOverlapWhenNoInteriorTargetBoundaryCanFragmentTheSource()
    {
        const ulong mib = 1024 * 1024;
        FileMeta[] files =
        [
            File("source.sst", 2, 1, "b", "y", 128 * mib, keyBoundsComplete: true),
            File("target.sst", 3, 2, "a", "zzzz", 768 * mib, keyBoundsComplete: true)
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l1TargetSizeBytes: 1),
            null,
            false,
            512L * 1024 * 1024);

        Assert.NotNull(plan);
        Assert.Equal(CompactionPurpose.Merge, plan.Purpose);
        Assert.Equal(2u, plan.SourceLevel);
        Assert.Equal(3u, plan.TargetLevel);
        Assert.Equal(["source.sst", "target.sst"], plan.Inputs.Select(static file => file.Name));
    }

    [Fact]
    public void ShouldKeepSourceBatchWhenWorkExactlyMatchesPreferredTarget()
    {
        const ulong mib = 1024 * 1024;
        FileMeta[] files =
        [
            File("source-0.sst", 2, 1, "b", "d", 128 * mib, keyBoundsComplete: true),
            File("source-1.sst", 2, 2, "e", "h", 128 * mib, keyBoundsComplete: true),
            File("source-2.sst", 2, 3, "i", "l", 128 * mib, keyBoundsComplete: true),
            File("source-3.sst", 2, 4, "m", "n", 32 * mib, keyBoundsComplete: true),
            File("target.sst", 3, 5, "a", "z", 128 * mib, keyBoundsComplete: true)
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 3, l1TargetSizeBytes: 40L * 1024 * 1024),
            null,
            false,
            512L * 1024 * 1024);

        Assert.NotNull(plan);
        Assert.Equal(CompactionPurpose.Merge, plan.Purpose);
        Assert.Equal(3, plan.Inputs.Count(file => file.Level == 2));
        Assert.Equal(1, plan.Inputs.Count(file => file.Level == 3));
        Assert.Equal(512UL * 1024 * 1024,
            plan.Inputs.Aggregate(0UL, static (total, file) => total + file.SizeBytes));
    }

    [Fact]
    public void ShouldShrinkSourceBatchWhenSingletonWorkFits()
    {
        const ulong mib = 1024 * 1024;
        FileMeta[] files =
        [
            File("source-0.sst", 2, 1, "b", "d", 128 * mib, keyBoundsComplete: true),
            File("source-1.sst", 2, 2, "e", "h", 128 * mib, keyBoundsComplete: true),
            File("source-2.sst", 2, 3, "i", "l", 128 * mib, keyBoundsComplete: true),
            File("source-3.sst", 2, 4, "z", "zz", 32 * mib, keyBoundsComplete: true),
            File("target-0.sst", 3, 5, "a", "d", 128 * mib, keyBoundsComplete: true),
            File("target-1.sst", 3, 6, "e", "h", 128 * mib, keyBoundsComplete: true),
            File("target-2.sst", 3, 7, "i", "l", 128 * mib, keyBoundsComplete: true),
            File("target-3.sst", 3, 8, "m", "p", 128 * mib, keyBoundsComplete: true),
            File("target-4.sst", 3, 9, "q", "t", 128 * mib, keyBoundsComplete: true),
            File("target-5.sst", 3, 10, "u", "x", 128 * mib, keyBoundsComplete: true)
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 3, l1TargetSizeBytes: 40L * 1024 * 1024),
            null,
            false,
            512L * 1024 * 1024);

        Assert.NotNull(plan);
        Assert.Equal(CompactionPurpose.Merge, plan.Purpose);
        Assert.Equal(["source-0.sst", "target-0.sst"], plan.Inputs.Select(static file => file.Name));
        Assert.Equal(256UL * 1024 * 1024,
            plan.Inputs.Aggregate(0UL, static (total, file) => total + file.SizeBytes));
    }

    [Fact]
    public void ShouldAdvanceFragmentationWhenPriorCutRemainsAnInclusiveBound()
    {
        const ulong mib = 1024 * 1024;
        var files = new List<FileMeta>
        {
            File("source-0.sst", 2, 1, "b", "m", 64 * mib, keyBoundsComplete: true),
            File("source-1.sst", 2, 2, "z", "zz", 128 * mib, keyBoundsComplete: true),
            File("source-2.sst", 2, 3, "zzz", "zzzz", 128 * mib, keyBoundsComplete: true),
            File("source-3.sst", 2, 4, "zzzzz", "zzzzzz", 128 * mib, keyBoundsComplete: true),
            File("target-0.sst", 3, 5, "a", "d", 128 * mib, keyBoundsComplete: true),
            File("target-1.sst", 3, 6, "e", "h", 128 * mib, keyBoundsComplete: true),
            File("target-2.sst", 3, 7, "i", "l", 128 * mib, keyBoundsComplete: true),
            File("target-3.sst", 3, 8, "m", "p", 128 * mib, keyBoundsComplete: true),
            File("target-4.sst", 3, 9, "q", "t", 128 * mib, keyBoundsComplete: true),
            File("target-5.sst", 3, 10, "u", "x", 128 * mib, keyBoundsComplete: true)
        };
        var configuration = Configuration(
            l0FileCountTrigger: 3,
            l1TargetSizeBytes: 40L * 1024 * 1024);

        var next = LeveledCompactionPlanner.Pick(
            files,
            0,
            configuration,
            null,
            false,
            512L * 1024 * 1024);
        files[0].LargestKey = TestBytes.FromString("i");
        files[0].SizeBytes = 32 * mib;
        var settled = LeveledCompactionPlanner.Pick(
            files,
            0,
            configuration,
            null,
            false,
            512L * 1024 * 1024);

        Assert.NotNull(next);
        Assert.Equal(CompactionPurpose.Fragment, next.Purpose);
        Assert.Equal(TestBytes.FromString("i"), next.FragmentSplitKey);
        Assert.NotNull(settled);
        Assert.Equal(CompactionPurpose.Merge, settled.Purpose);
        Assert.Equal("source-0.sst", Assert.Single(settled.Inputs, file => file.Level == 2).Name);
        Assert.Equal(3, settled.Inputs.Count(file => file.Level == 3));
        Assert.True(settled.Inputs.Aggregate(0UL, static (total, file) => total + file.SizeBytes)
                    <= 512UL * 1024 * 1024);
    }

    [Fact]
    public void ShouldKeepEqualityChainWhenNoFurtherInteriorSplitExists()
    {
        const ulong mib = 1024 * 1024;
        var files = new List<FileMeta>
        {
            File("source-0.sst", 2, 1, "b", "d", 128 * mib, keyBoundsComplete: true),
            File("source-1.sst", 2, 2, "z", "zz", 128 * mib, keyBoundsComplete: true),
            File("source-2.sst", 2, 3, "zzz", "zzzz", 128 * mib, keyBoundsComplete: true),
            File("source-3.sst", 2, 4, "zzzzz", "zzzzzz", 32 * mib, keyBoundsComplete: true),
            File("target-0.sst", 3, 5, "a", "d", 128 * mib, keyBoundsComplete: true),
            File("target-1.sst", 3, 6, "d", "h", 128 * mib, keyBoundsComplete: true),
            File("target-2.sst", 3, 7, "h", "l", 128 * mib, keyBoundsComplete: true),
            File("target-3.sst", 3, 8, "l", "p", 128 * mib, keyBoundsComplete: true),
            File("target-4.sst", 3, 9, "p", "t", 128 * mib, keyBoundsComplete: true),
            File("target-5.sst", 3, 10, "t", "x", 128 * mib, keyBoundsComplete: true)
        };

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 3, l1TargetSizeBytes: 40L * 1024 * 1024),
            null,
            false,
            512L * 1024 * 1024);

        Assert.NotNull(plan);
        Assert.Equal(CompactionPurpose.Merge, plan.Purpose);
        Assert.Equal("source-0.sst", Assert.Single(plan.Inputs, file => file.Level == 2).Name);
        Assert.Equal(6, plan.Inputs.Count(file => file.Level == 3));
        Assert.True(plan.Inputs.Aggregate(0UL, static (total, file) => total + file.SizeBytes)
                    > 512UL * 1024 * 1024);
    }

    [Fact]
    public void ShouldTreatOverflowingLevelWorkEstimateAsOverfull()
    {
        FileMeta[] files =
        [
            File("first.sst", 2, 1, "a", "b", ulong.MaxValue, keyBoundsComplete: true),
            File("second.sst", 2, 2, "c", "d", 1, keyBoundsComplete: true)
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l1TargetSizeBytes: 1024),
            null,
            false,
            512L * 1024 * 1024);

        Assert.NotNull(plan);
        Assert.Equal("first.sst", Assert.Single(plan.Inputs).Name);
    }

    [Fact]
    public void ShouldKeepExistingPlanWhenPreferredWorkTargetIsDisabled()
    {
        const ulong mib = 1024 * 1024;
        var files = new List<FileMeta>
        {
            File("source-0.sst", 2, 1, "b", "y", 128 * mib, keyBoundsComplete: true),
            File("source-1.sst", 2, 2, "z", "zz", 128 * mib, keyBoundsComplete: true),
            File("source-2.sst", 2, 3, "zzz", "zzzz", 128 * mib, keyBoundsComplete: true),
            File("source-3.sst", 2, 4, "zzzzz", "zzzzzz", 32 * mib, keyBoundsComplete: true)
        };
        files.AddRange(new[]
        {
            File("target-0.sst", 3, 5, "a", "d", 128 * mib, keyBoundsComplete: true),
            File("target-1.sst", 3, 6, "e", "h", 128 * mib, keyBoundsComplete: true),
            File("target-2.sst", 3, 7, "i", "l", 128 * mib, keyBoundsComplete: true),
            File("target-3.sst", 3, 8, "m", "p", 128 * mib, keyBoundsComplete: true),
            File("target-4.sst", 3, 9, "q", "t", 128 * mib, keyBoundsComplete: true),
            File("target-5.sst", 3, 10, "u", "x", 128 * mib, keyBoundsComplete: true)
        });

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 3, l1TargetSizeBytes: 40L * 1024 * 1024),
            null,
            false);

        Assert.NotNull(plan);
        Assert.Equal(CompactionPurpose.Merge, plan.Purpose);
        Assert.Equal(3, plan.Inputs.Count(file => file.Level == 2));
        Assert.Equal(6, plan.Inputs.Count(file => file.Level == 3));
    }

    [Fact]
    public void ShouldKeepCompleteOverlapWhenFragmentationBoundsAreUntrustedOrOverlapping()
    {
        const ulong mib = 1024 * 1024;
        FileMeta[] baseline =
        [
            File("source-0.sst", 2, 1, "b", "y", 128 * mib, keyBoundsComplete: true),
            File("source-1.sst", 2, 2, "p", "zz", 128 * mib, keyBoundsComplete: true),
            File("source-2.sst", 2, 3, "zzz", "zzzz", 128 * mib, keyBoundsComplete: true),
            File("source-3.sst", 2, 4, "zzzzz", "zzzzzz", 32 * mib, keyBoundsComplete: true),
            File("target-0.sst", 3, 5, "a", "d", 128 * mib, keyBoundsComplete: true),
            File("target-1.sst", 3, 6, "e", "h", 128 * mib, keyBoundsComplete: false),
            File("target-2.sst", 3, 7, "i", "l", 128 * mib, keyBoundsComplete: true),
            File("target-3.sst", 3, 8, "m", "p", 128 * mib, keyBoundsComplete: true),
            File("target-4.sst", 3, 9, "q", "t", 128 * mib, keyBoundsComplete: true),
            File("target-5.sst", 3, 10, "u", "x", 128 * mib, keyBoundsComplete: true)
        ];

        for (var scenario = 0; scenario < 4; scenario++)
        {
            var files = baseline.Select(static file => file.Clone()).ToArray();
            switch (scenario)
            {
                case 0:
                    files[0].KeyBoundsComplete = false;
                    break;
                case 1:
                    files[5].KeyBoundsComplete = false;
                    break;
                case 2:
                    files[1].SmallestKey = TestBytes.FromString("p");
                    break;
                case 3:
                    files[5].SmallestKey = TestBytes.FromString("c");
                    break;
            }

            var plan = LeveledCompactionPlanner.Pick(
                files,
                0,
                Configuration(l0FileCountTrigger: 3, l1TargetSizeBytes: 40L * 1024 * 1024),
                null,
                false,
                512L * 1024 * 1024);

            Assert.NotNull(plan);
            Assert.Equal(CompactionPurpose.Merge, plan.Purpose);
            Assert.Equal(2u, plan.SourceLevel);
            Assert.Equal(3u, plan.TargetLevel);
            Assert.Equal(1, plan.Inputs.Count(file => file.Level == 2));
            Assert.Equal(6, plan.Inputs.Count(file => file.Level == 3));
        }
    }

    [Fact]
    public void ShouldAbsorbTransitivelyOverlappingTargetFiles()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "b", "c"),
            File("l1-a.sst", 1, 2, "c", "d"),
            File("l1-b.sst", 1, 3, "d", "e"),
            File("l1-unrelated.sst", 1, 4, "x", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files, 0, Configuration(l0FileCountTrigger: 1), null, false);

        Assert.NotNull(plan);
        Assert.Equal(["l0-1.sst", "l1-a.sst", "l1-b.sst"],
            plan.Inputs.Select(static file => file.Name));
    }

    [Fact]
    public void ShouldDetectWhenThePlannedTargetSpanChanges()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "b", "c"),
            File("l0-2.sst", 0, 2, "d", "e"),
            File("l1-overlap.sst", 1, 3, "c", "d")
        ];
        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 2),
            null,
            true);

        Assert.NotNull(plan);
        Assert.True(LeveledCompactionPlanner.HasSameTargetSpan(
            plan,
            [.. files, File("l1-unrelated.sst", 1, 4, "x", "z")]));
        Assert.False(LeveledCompactionPlanner.HasSameTargetSpan(
            plan,
            [.. files, File("l1-new-overlap.sst", 1, 5, "e", "f")]));
    }

    [Fact]
    public void ShouldForceL0CompactionBelowNormalTriggers()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "b"),
            File("l0-2.sst", 0, 2, "c", "d")
        ];

        var plan = LeveledCompactionPlanner.Pick(files, 0, Configuration(), null, true);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Inputs.Count);
    }

    [Fact]
    public void ShouldNotForceSingleL0File()
    {
        var plan = LeveledCompactionPlanner.Pick(
            [File("l0-1.sst", 0, 1, "a", "b")], 0, Configuration(), null, true);

        Assert.Null(plan);
    }

    [Fact]
    public void ShouldForceInnerLevelCompactionBelowNormalTrigger()
    {
        FileMeta[] files =
        [
            File("l1-1.sst", 1, 1, "a", "b"),
            File("l1-2.sst", 1, 2, "c", "d")
        ];

        var plan = LeveledCompactionPlanner.Pick(files, 0, Configuration(), null, true);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan.SourceLevel);
    }

    [Fact]
    public void ShouldShrinkSourcesToTheInputLimitWhenForced()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z"),
            File("l0-2.sst", 0, 2, "a", "z"),
            File("l1-1.sst", 1, 3, "a", "m")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files, 0, Configuration(maximumInputFiles: 2), null, true);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Inputs.Count);
        Assert.Equal("l0-1.sst", Assert.Single(plan.Inputs, file => file.Level == 0).Name);
    }

    [Fact]
    public void ShouldTriggerL0CompactionByBytesIndependentlyOfFileCount()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "z", 1025)
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(1024, 10),
            null,
            false);

        Assert.NotNull(plan);
    }

    [Fact]
    public void ShouldTriggerInnerLevelByConfiguredSizeRatio()
    {
        FileMeta[] files =
        [
            File("l1-1.sst", 1, 1, "a", "m", 1025),
            File("l2-1.sst", 2, 2, "n", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l1TargetSizeBytes: 1024, levelMultiplier: 3),
            null,
            false);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan.SourceLevel);
        Assert.Equal(2u, plan.TargetLevel);
    }

    [Fact]
    public void ShouldMakePointAndRangeTombstoneEligibilityDistinct()
    {
        FileMeta[] files =
        [
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l0-unselected.sst", 0, 2, "x", "z")
        ];

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            Configuration(l0FileCountTrigger: 1, maximumInputFiles: 1),
            7,
            false);

        Assert.NotNull(plan);
        Assert.True(plan.PointTombstoneGcEligible);
        Assert.False(plan.RangeTombstoneGcEligible);
    }

    [Fact]
    public void ShouldRejectInvalidFileMetadataAsCorruption()
    {
        var invalid = File("invalid.sst", 7, 1, "z", "a");

        var exception = Assert.ThrowsAny<PantsException>(() =>
            LeveledCompactionPlanner.Pick(
                [invalid],
                0,
                Configuration(),
                null,
                false));

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public void ShouldProduceTheSamePlanForTheSameMetadataSnapshot()
    {
        FileMeta[] files =
        [
            File("l0-2.sst", 0, 2, "d", "f"),
            File("l0-1.sst", 0, 1, "a", "c"),
            File("l1-1.sst", 1, 3, "b", "e")
        ];
        var configuration = Configuration(l0FileCountTrigger: 2);

        var first = LeveledCompactionPlanner.Pick(
            files, 0, configuration, 5, false);
        var second = LeveledCompactionPlanner.Pick(
            files, 0, configuration, 5, false);

        Assert.Equal(first?.SourceLevel, second?.SourceLevel);
        Assert.Equal(first?.TargetLevel, second?.TargetLevel);
        Assert.Equal(
            first?.Inputs.Select(static file => file.Name),
            second?.Inputs.Select(static file => file.Name));
    }

    [Fact]
    public void ShouldDrainSingleL0FileWhenDebtDrainPicks()
    {
        var plan = LeveledCompactionPlanner.PickDebtDrain(
            [File("l0-1.sst", 0, 1, "a", "b")], 0, Configuration(), null);

        Assert.NotNull(plan);
        Assert.Equal(0u, plan.SourceLevel);
    }

    [Fact]
    public void ShouldNotRewriteUnderTargetInnerLevelsWhenDebtDrainPicks()
    {
        FileMeta[] files =
        [
            File("l1-1.sst", 1, 1, "a", "b"),
            File("l1-2.sst", 1, 2, "c", "d")
        ];

        Assert.Null(LeveledCompactionPlanner.PickDebtDrain(files, 0, Configuration(), null));
    }

    [Fact]
    public void ShouldReportDebtClearOnlyWithoutL0OrOverfullInnerLevel()
    {
        var configuration = Configuration(l1TargetSizeBytes: 2048);

        Assert.True(CompactionDebt.IsClear([File("l1.sst", 1, 1, "a", "b", 1024)], 0, configuration));
        Assert.False(CompactionDebt.IsClear([File("l0.sst", 0, 1, "a", "b")], 0, configuration));
        Assert.False(CompactionDebt.IsClear(
            [File("l1-1.sst", 1, 1, "a", "b", 1024), File("l1-2.sst", 1, 2, "c", "d", 2048)],
            0,
            configuration));
    }

    static PantsCompactionConfiguration Configuration(
        long l0SizeTriggerBytes = 4L * 1024 * 1024,
        int l0FileCountTrigger = 4,
        int maximumInputFiles = 64,
        int levelMultiplier = 10,
        long l1TargetSizeBytes = 40L * 1024 * 1024) => new(
        l0SizeTriggerBytes,
        l0FileCountTrigger,
        maximumInputFiles,
        levelMultiplier,
        l1TargetSizeBytes);

    static FileMeta File(
        string name,
        uint level,
        ulong sequence,
        string smallest,
        string largest,
        ulong sizeBytes = 1024,
        uint columnFamilyId = 0,
        bool keyBoundsComplete = false) => new()
        {
            Name = name,
            Level = level,
            ColumnFamilyId = columnFamilyId,
            SstSequence = sequence,
            SizeBytes = sizeBytes,
            SmallestKey = TestBytes.FromString(smallest).ToArray(),
            LargestKey = TestBytes.FromString(largest).ToArray(),
            KeyBoundsComplete = keyBoundsComplete
        };
}
