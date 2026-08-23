namespace Cntryl.Pants.Tests;

[Collection(RuntimeDiagnosticsTestGroup.Name)]
public sealed class PantsColumnFamilyReclamationTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReclaimDroppedColumnFamilyFilesWithoutASnapshotPin(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(CreateOptions(
            directory.Path,
            simulatedCloud));
        IPantsColumnFamily family = await CreateFlushedFamilyAsync(database, "reclaim-now");
        Assert.NotEmpty(FamilyFiles(directory.Path, family.Id, simulatedCloud));

        await database.DropColumnFamilyAsync(family);

        Assert.Empty(FamilyFiles(directory.Path, family.Id, simulatedCloud));
        Assert.DoesNotContain(
            (await database.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files),
            file => file.ColumnFamilyId == family.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDeferReclamationUntilTheOldestSnapshotReleases(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(CreateOptions(
            directory.Path,
            simulatedCloud));
        IPantsColumnFamily family = await CreateFlushedFamilyAsync(database, "reclaim-later");
        await using IPantsTransaction snapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);

        await database.DropColumnFamilyAsync(family);

        Assert.Equal(
            "value",
            TestBytes.ToText((await snapshot.GetAsync(TestBytes.FromString("key")))!.Value));
        Assert.NotEmpty(FamilyFiles(directory.Path, family.Id, simulatedCloud));
        await snapshot.RollbackAsync();
        Assert.Empty(FamilyFiles(directory.Path, family.Id, simulatedCloud));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDeferReclamationWhileOnlineVerificationOwnsBarrier(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        var verifierStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerifier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PantsStorageVerificationDelegate verifier = async (_, _) =>
        {
            verifierStarted.SetResult();
            await releaseVerifier.Task;
            return new PantsStorageVerificationReport(
                0,
                0,
                0,
                0,
                0,
                null,
                0,
                0,
                0,
                true,
                PantsEngineHealth.Healthy,
                []);
        };
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, simulatedCloud),
            new PantsRuntimeDependencies(storageVerifier: verifier));
        var family = await CreateFlushedFamilyAsync(database, "verification-reclamation");
        await using var snapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        await database.DropColumnFamilyAsync(family);
        var verification = database.VerifyStorageAsync(TimeSpan.FromSeconds(2)).AsTask();

        try
        {
            await verifierStarted.Task.WaitAsync(AssertionTimeout);
            await snapshot.RollbackAsync();
            Assert.NotEmpty(FamilyFiles(directory.Path, family.Id, simulatedCloud));
        }
        finally
        {
            releaseVerifier.TrySetResult();
        }

        Assert.Equal(!simulatedCloud, (await verification).Authoritative);
        await WaitForFamilyReclamationAsync(
            directory.Path,
            family.Id,
            simulatedCloud);
    }

    [Fact]
    public async Task ShouldPreserveVerificationResultGivenDeferredReclamationFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new ArmableFailpointHandler();
        var verifierStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerifier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PantsStorageVerificationDelegate verifier = async (_, _) =>
        {
            verifierStarted.SetResult();
            await releaseVerifier.Task;
            return new PantsStorageVerificationReport(
                0,
                0,
                0,
                0,
                0,
                null,
                0,
                0,
                0,
                true,
                PantsEngineHealth.Healthy,
                []);
        };
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, simulatedCloud: true),
            new PantsRuntimeDependencies(failpoint, verifier));
        var family = await CreateFlushedFamilyAsync(database, "failed-reclamation");
        await using var snapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        await database.DropColumnFamilyAsync(family);
        var verification = database.VerifyStorageAsync(TimeSpan.FromSeconds(2)).AsTask();

        try
        {
            await verifierStarted.Task.WaitAsync(AssertionTimeout);
            await snapshot.RollbackAsync();
            failpoint.Arm(PantsFailpoint.BeforeCloudSstGarbageCollectionDelete);
        }
        finally
        {
            releaseVerifier.TrySetResult();
        }

        Assert.Equal(PantsEngineHealth.Healthy, (await verification).Health);
        using var timeout = new CancellationTokenSource(AssertionTimeout);
        while ((await database.GetRuntimeMetricsAsync(timeout.Token)).Health ==
               PantsEngineHealth.Healthy)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
    }

    [Fact]
    public async Task ShouldRestoreDeferredReclamationBeforeAdmittingNextVerification()
    {
        using var directory = new TemporaryDirectory();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var familyId = 0u;
        var filesSeenBySecondVerifier = -1;
        var invocations = 0;
        PantsStorageVerificationDelegate verifier = async (_, _) =>
        {
            if (Interlocked.Increment(ref invocations) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            else
            {
                Volatile.Write(
                    ref filesSeenBySecondVerifier,
                    FamilyFiles(directory.Path, familyId, simulatedCloud: false).Length);
            }

            return new PantsStorageVerificationReport(
                0,
                0,
                0,
                0,
                0,
                null,
                0,
                0,
                0,
                true,
                PantsEngineHealth.Healthy,
                []);
        };
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, simulatedCloud: false),
            new PantsRuntimeDependencies(storageVerifier: verifier));
        var family = await CreateFlushedFamilyAsync(database, "ordered-reclamation");
        familyId = family.Id;
        await using var snapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        await database.DropColumnFamilyAsync(family);
        var firstVerification = database.VerifyStorageAsync(TimeSpan.FromSeconds(5)).AsTask();

        try
        {
            await firstStarted.Task.WaitAsync(AssertionTimeout);
            await snapshot.RollbackAsync();
            var secondVerification = database
                .VerifyStorageAsync(TimeSpan.FromSeconds(5))
                .AsTask();
            releaseFirst.TrySetResult();
            _ = await firstVerification;
            _ = await secondVerification;
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        Assert.Equal(2, Volatile.Read(ref invocations));
        Assert.Equal(0, Volatile.Read(ref filesSeenBySecondVerifier));
    }

    static async ValueTask<IPantsColumnFamily> CreateFlushedFamilyAsync(
        IPantsDatabase database,
        string name)
    {
        IPantsColumnFamily family = await database.CreateColumnFamilyAsync(name);
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
        await transaction.CommitAsync(
            database.Options.Storage is PantsStorageConfiguration.SimulatedCloud
                ? PantsWriteOptions.CloudStrict
                : PantsWriteOptions.Buffered);
        await database.FlushAsync(family);
        return family;
    }

    static PantsOpenOptions CreateOptions(string path, bool simulatedCloud) =>
        (simulatedCloud
            ? PantsOpenOptions.SimulatedCloud(path, "reclamation", "cf/")
            : PantsOpenOptions.Local(path))
        .WithBackgroundCompaction(false);

    static async Task WaitForFamilyReclamationAsync(
        string path,
        uint familyId,
        bool simulatedCloud)
    {
        using var timeout = new CancellationTokenSource(AssertionTimeout);
        while (FamilyFiles(path, familyId, simulatedCloud).Length != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
    }

    static string[] FamilyFiles(string path, uint familyId, bool simulatedCloud)
    {
        string pattern = $"{familyId:000000}_*.sst";
        string[] local = Directory.GetFiles(Path.Combine(path, "sst"), pattern);
        return simulatedCloud
            ? [.. local, .. Directory.GetFiles(Path.Combine(path, "cloud_store", "sst"), pattern)]
            : local;
    }
}
