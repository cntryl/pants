using Cntryl.Pants.Cloud.Internal.Objects;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class SimulatedCloudCatalogPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveSuccessorCatalogWhenAnAlreadyValidatedOldPublicationCompletes(bool primaryPublished)
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using (var seed = LocalDiskStore.Open(directory.Path, state))
        {
        }
        var objects = new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"));
        var now = DateTimeOffset.UtcNow;
        using var oldLease = new CloudLeaseCoordinator(
            new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey),
            new ManualClock(now), "old", TimeSpan.FromSeconds(10), TimeSpan.Zero);
        var oldEpoch = await oldLease.AcquireAsync(CancellationToken.None);
        using var blocked = new FlushPipelineFailpointHandler(primaryPublished
            ? Failpoint.BeforeSimulatedCloudCatalogMirrorWrite
            : Failpoint.BeforeSimulatedCloudCatalogWrite, armOnConstruction: false);
        await using var old = new SimulatedCloudPersistence(directory.Path, oldEpoch, blocked, lease: oldLease);
        var oldSegment = TestWalSegment.Create(1, oldEpoch, 1, "old.wal", "old"u8.ToArray());
        blocked.Arm();
        var publication = Task.Run(() => old.PublishWal(oldSegment));
        try
        {
            await blocked.WaitUntilEnteredAsync(TestTimeouts.Expected);
            using var successorLease = new CloudLeaseCoordinator(
                new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey),
                new ManualClock(now + TimeSpan.FromSeconds(20)), "successor",
                TimeSpan.FromSeconds(10), TimeSpan.Zero);
            var successorEpoch = await successorLease.AcquireAsync(CancellationToken.None);
            Assert.True(successorEpoch > oldEpoch);
            await using var successor = new SimulatedCloudPersistence(directory.Path, successorEpoch, lease: successorLease);
            var segment = TestWalSegment.Create(primaryPublished ? 2UL : 1UL, successorEpoch,
                primaryPublished ? 2UL : 1UL, "successor.wal", "successor"u8.ToArray(),
                primaryPublished ? oldSegment.Facts.Link : WalChainLink.Genesis);
            successor.PublishWal(segment);
            var primary = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogObjectKey));
            var mirror = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey));

            blocked.Release();
            var failure = await Record.ExceptionAsync(() => publication);
            Assert.Equal(primary.Data.ToArray(), (await objects.GetAsync(PantsCloudObjectLayout.WalCatalogObjectKey))?.Data.ToArray());
            Assert.Equal(mirror.Data.ToArray(), (await objects.GetAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey))?.Data.ToArray());
            if (primaryPublished)
            {
                Assert.IsType<PantsLeaseIndeterminateException>(failure);
                Assert.True(old.HasPersistenceAnomaly);
            }
            else
            {
                Assert.IsType<PantsFencedException>(failure);
            }

            var catalog = ProviderCloudPersistence.DecodeCatalogForFloor(primary.Data.Span);
            Assert.Equal(successorEpoch, catalog.FencingEpoch);
            Assert.True(segment.Facts.Link.Matches(catalog.Segments.Values.Last().Facts!.Link));
        }
        finally
        {
            blocked.Release();
            _ = await Record.ExceptionAsync(() => publication.WaitAsync(TestTimeouts.Expected));
        }
    }

    [Fact]
    public async Task ShouldLeaveCatalogProbesReadOnlyAndRepairAChangedMirrorOnlyDuringOwnedPublication()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using (var seed = LocalDiskStore.Open(directory.Path, state))
        {
        }
        await using var persistence = new SimulatedCloudPersistence(directory.Path, 1);
        var objects = new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"));
        var invalidMirror = "{\"format_version\":4}"u8.ToArray();
        Assert.True(await objects.PutAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
            invalidMirror, new CloudObjectWriteCondition.Unconditional()));

        Assert.Equal(1UL, SimulatedCloudPersistence.ReadStartupFencingEpoch(directory.Path));
        await persistence.ValidateWriteAuthorityAsync(CancellationToken.None);
        Assert.Equal(invalidMirror, (await objects.GetAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey))?.Data.ToArray());

        persistence.PublishWal(TestWalSegment.Create(1, 1, 1, "owned.wal", "owned"u8.ToArray()));
        var primary = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogObjectKey));
        var mirror = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey));
        Assert.Equal(primary.Data.ToArray(), mirror.Data.ToArray());
        Assert.Single(ProviderCloudPersistence.DecodeCatalogForFloor(primary.Data.Span).Segments);
    }

    [Fact]
    public async Task ShouldRepairAnObservedTornPrimaryFromItsMirrorOnlyAfterAcquiringTheNextLease()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using (var seed = LocalDiskStore.Open(directory.Path, state))
        {
        }
        await using (var initial = new SimulatedCloudPersistence(directory.Path, 1))
        {
        }
        var objects = new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"));
        var torn = "{torn"u8.ToArray();
        Assert.True(await objects.PutAsync(PantsCloudObjectLayout.WalCatalogObjectKey,
            torn, new CloudObjectWriteCondition.Unconditional()));
        Assert.Equal(1UL, SimulatedCloudPersistence.ReadStartupFencingEpoch(directory.Path));
        Assert.Equal(torn, (await objects.GetAsync(PantsCloudObjectLayout.WalCatalogObjectKey))?.Data.ToArray());

        await using var repaired = new SimulatedCloudPersistence(directory.Path, 2);

        var primary = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogObjectKey));
        var mirror = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey));
        Assert.Equal(primary.Data.ToArray(), mirror.Data.ToArray());
        Assert.Equal(2UL, ProviderCloudPersistence.DecodeCatalogForFloor(primary.Data.Span).FencingEpoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReportUnknownAfterCatalogEffectsAndConvergeTheExactBatchOnRetry(bool mirrorWritten)
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using (var seed = LocalDiskStore.Open(directory.Path, state))
        {
        }
        var failpoints = new ArmableFailpointHandler();
        await using var persistence = new SimulatedCloudPersistence(directory.Path, 1, failpoints);
        var segment = TestWalSegment.Create(1, 1, 1, "retained.wal", "retained"u8.ToArray());
        failpoints.Arm(mirrorWritten
            ? Failpoint.AfterSimulatedCloudCatalogMirrorWrite
            : Failpoint.AfterSimulatedCloudCatalogPrimaryWrite);

        Assert.Throws<PantsLeaseIndeterminateException>(() => persistence.PublishWal(segment));

        var objects = new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"));
        var committed = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogObjectKey));
        Assert.True(persistence.HasPersistenceAnomaly);
        persistence.PublishWal(segment);
        var after = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogObjectKey));
        var mirror = Assert.IsType<CloudObject>(await objects.GetAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey));
        Assert.Equal(committed.Data.ToArray(), after.Data.ToArray());
        Assert.Equal(after.Data.ToArray(), mirror.Data.ToArray());
        Assert.Single(ProviderCloudPersistence.DecodeCatalogForFloor(after.Data.Span).Segments);
    }
}
