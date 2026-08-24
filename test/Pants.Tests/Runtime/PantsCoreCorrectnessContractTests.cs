namespace Cntryl.Pants.Tests.Runtime;

public sealed class PantsCoreCorrectnessContractTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldMapMissingVerificationFileToNotFound()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(storageVerifier: (_, _) =>
                throw new FileNotFoundException("Required SST is missing.")));

        var failure = await Assert.ThrowsAsync<PantsNotFoundException>(() =>
            database.VerifyStorageAsync(AssertionTimeout).AsTask());

        Assert.Equal(PantsErrorCode.NotFound, failure.Code);
        Assert.IsType<FileNotFoundException>(failure.InnerException);
    }

    [Fact]
    public void ShouldPreserveNoSpaceClassificationWhenMappingIoFailures()
    {
        var failure = new IOException("The disk full condition was reached.");

        var mapped = PantsException.FromIOException(failure);

        Assert.IsType<PantsNoSpaceException>(mapped);
        Assert.Equal(PantsErrorCode.NoSpace, mapped.Code);
        Assert.Same(failure, mapped.InnerException);
    }

    [Fact]
    public async Task ShouldReportNoPinnedSstsWhenSnapshotRetainsNoObsoleteFiles()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (var write = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            write.Put("key"u8.ToArray(), "value"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using var snapshot = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.True(metrics.SstCount > 0);
        Assert.Equal(0, metrics.PinnedSsts);
    }

    [Fact]
    public async Task ShouldNotFabricateScanCacheMissesOrRangeTombstonesFromCandidateSsts()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (var write = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            write.Put("key"u8.ToArray(), "value"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        var before = await database.GetReadPathDiagnosticsAsync();
        await using (var read = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        await using (var scan = await read.ScanAsync(new PantsScanQuery()))
        {
            await foreach (var _ in scan)
            {
            }
        }

        var after = await database.GetReadPathDiagnosticsAsync();

        Assert.True(after.CandidateSstFilesChecked > before.CandidateSstFilesChecked);
        Assert.Equal(before.SstReaderCacheMisses, after.SstReaderCacheMisses);
        Assert.Equal(before.RangeTombstoneScans, after.RangeTombstoneScans);
    }
}
