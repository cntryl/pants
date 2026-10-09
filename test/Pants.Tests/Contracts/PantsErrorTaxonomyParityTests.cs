using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsErrorTaxonomyParityTests
{
    [Fact]
    public async Task ShouldReportMissingVerificationFileAsTransientIo()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(storageVerifier: (_, _) =>
                throw new FileNotFoundException("Required SST is missing.")));

        var failure = await Assert.ThrowsAsync<PantsIOException>(() =>
            database.PersistentStorage!.VerifyAsync(TestTimeouts.Expected).AsTask());

        Assert.Equal(PantsErrorCode.Io, failure.Code);
        Assert.Equal(PantsErrorSeverity.Transient, failure.Severity);
        Assert.IsType<FileNotFoundException>(failure.InnerException);
    }

    [Fact]
    public void ShouldMapMissingDirectoryToTransientIo()
    {
        var failure = new DirectoryNotFoundException("The database directory vanished.");

        var mapped = PantsException.FromIOException(failure);

        Assert.IsType<PantsIOException>(mapped);
        Assert.Equal(PantsErrorSeverity.Transient, mapped.Severity);
        Assert.Same(failure, mapped.InnerException);
    }

    [Fact]
    public async Task ShouldReportZeroVerificationDeadlineAsTimeout()
    {
        using var directory = new TemporaryDirectory();
        var verifierCalls = 0;
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(storageVerifier: (_, _) =>
            {
                Interlocked.Increment(ref verifierCalls);
                throw new InvalidOperationException("Verification must not start.");
            }));

        var failure = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
            database.PersistentStorage!.VerifyAsync(TimeSpan.Zero).AsTask());

        Assert.Equal(PantsErrorCode.Timeout, failure.Code);
        Assert.Equal(0, Volatile.Read(ref verifierCalls));
    }

    [Fact]
    public async Task ShouldStillRejectNegativeVerificationDeadline()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.PersistentStorage!.VerifyAsync(TimeSpan.FromMilliseconds(-5)).AsTask());
    }

    [Fact]
    public async Task ShouldReportZeroShutdownDeadlineAsRetryableTimeout()
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        try
        {
            var failure = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                database.ShutdownAsync(TimeSpan.Zero).AsTask());

            Assert.Equal(PantsErrorCode.Timeout, failure.Code);
        }
        finally
        {
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
    }

    [Fact]
    public async Task ShouldStillRejectNegativeShutdownDeadline()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ShutdownAsync(TimeSpan.FromMilliseconds(-5)).AsTask());
    }

    [Fact]
    public async Task ShouldOpenLocalWriterWhenClockSkewEqualsLeaseTimeToLive()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions
            .Local(directory.Path)
            .WithLeaseClockSkewTolerance(TimeSpan.FromSeconds(30))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(30)));

        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
    }

    [Fact]
    public async Task ShouldOpenSimulatedCloudWriterWhenClockSkewEqualsLeaseTimeToLive()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions
            .SimulatedCloud(directory.Path, "bucket", "prefix")
            .WithLeaseClockSkewTolerance(TimeSpan.FromSeconds(30))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(30)));

        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
    }
}
