using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsStorageVerificationOutcomeTests
{
    [Fact]
    public async Task ShouldReportHealthyWithExitCodeZeroGivenHealthyStorage()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);

        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(directory.Path);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Healthy, outcome.Kind);
        Assert.Equal(0, outcome.ExitCode);
        Assert.NotNull(outcome.Report);
        Assert.Equal(PantsEngineHealth.Healthy, outcome.Report.Health);
        Assert.Null(outcome.ErrorCode);
        Assert.Null(outcome.PathFailure);
        Assert.Null(outcome.Message);
    }

    [Fact]
    public async Task ShouldReportDegradedWithExitCodeOneGivenUnownedSst()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        Directory.CreateDirectory(Path.Combine(directory.Path, "sst"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "sst", "orphan.sst"), [1]);

        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(directory.Path);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Degraded, outcome.Kind);
        Assert.Equal(1, outcome.ExitCode);
        Assert.NotNull(outcome.Report);
        Assert.Equal(PantsEngineHealth.Degraded, outcome.Report.Health);
    }

    [Fact]
    public async Task ShouldReportStorageMissingGivenNonexistentPath()
    {
        using var directory = new TemporaryDirectory();
        var missing = Path.Combine(directory.Path, "absent");

        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(missing);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Storage, outcome.Kind);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(PantsStoragePathFailure.Missing, outcome.PathFailure);
        Assert.Equal(PantsErrorCode.NotFound, outcome.ErrorCode);
        Assert.Null(outcome.Report);
        Assert.Contains("does not exist", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldReportStorageNotADirectoryGivenFilePath()
    {
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "plain-file");
        await File.WriteAllTextAsync(file, "not a database");

        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(file);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Storage, outcome.Kind);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(PantsStoragePathFailure.NotADirectory, outcome.PathFailure);
        Assert.Equal(PantsErrorCode.InvalidPath, outcome.ErrorCode);
        Assert.Contains("is not a directory", outcome.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0path")]
    public async Task ShouldReportStorageInvalidGivenMalformedPath(string path)
    {
        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(path);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Storage, outcome.Kind);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(PantsStoragePathFailure.Invalid, outcome.PathFailure);
        Assert.Equal(PantsErrorCode.InvalidPath, outcome.ErrorCode);
    }

    [Fact]
    public async Task ShouldReportStorageInaccessibleGivenUnreadableDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var database = Path.Combine(directory.Path, "db");
        Directory.CreateDirectory(database);
        await WriteEmptyFixtureAsync(database);
        File.SetUnixFileMode(database, UnixFileMode.None);
        try
        {
            if (CanStillEnumerate(database))
            {
                return; // Privileged users bypass permission bits; the condition cannot be produced.
            }

            var outcome = await PantsDatabase.VerifyPathOutcomeAsync(database);

            Assert.Equal(PantsStorageVerificationOutcomeKind.Storage, outcome.Kind);
            Assert.Equal(3, outcome.ExitCode);
            Assert.Equal(PantsStoragePathFailure.Inaccessible, outcome.PathFailure);
            Assert.Equal(PantsErrorCode.Io, outcome.ErrorCode);
            Assert.Contains("is inaccessible", outcome.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(
                database,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ShouldReportStorageInaccessibleGivenUnreadableManifestJournal()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var journal = Path.Combine(directory.Path, "manifest.journal");
        File.SetUnixFileMode(journal, UnixFileMode.None);
        if (CanStillRead(journal))
        {
            return; // Privileged users bypass permission bits; the condition cannot be produced.
        }

        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(directory.Path);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Storage, outcome.Kind);
        Assert.Equal(PantsStoragePathFailure.Inaccessible, outcome.PathFailure);
        Assert.Contains("manifest.journal", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldReportCorruptionWithExitCodeFourGivenMissingFormatMarker()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        File.Delete(Path.Combine(directory.Path, "FORMAT"));

        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(directory.Path);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Corruption, outcome.Kind);
        Assert.Equal(4, outcome.ExitCode);
        Assert.Equal(PantsErrorCode.CompatibilityError, outcome.ErrorCode);
        Assert.Null(outcome.PathFailure);
        Assert.Null(outcome.Report);
        Assert.False(string.IsNullOrEmpty(outcome.Message));
    }

    [Fact]
    public async Task ShouldReportCorruptionGivenMissingManifestJournal()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        File.Delete(Path.Combine(directory.Path, "manifest.journal"));

        var outcome = await PantsDatabase.VerifyPathOutcomeAsync(directory.Path);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Corruption, outcome.Kind);
        Assert.Equal(PantsErrorCode.Corruption, outcome.ErrorCode);
    }

    [Fact]
    public async Task ShouldHonorCancellationGivenCanceledToken()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PantsDatabase.VerifyPathOutcomeAsync(directory.Path, cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData(PantsEngineHealth.Healthy, PantsStorageVerificationOutcomeKind.Healthy)]
    [InlineData(PantsEngineHealth.Degraded, PantsStorageVerificationOutcomeKind.Degraded)]
    [InlineData(PantsEngineHealth.SalvageMode, PantsStorageVerificationOutcomeKind.Degraded)]
    [InlineData(PantsEngineHealth.WriteStalled, PantsStorageVerificationOutcomeKind.Degraded)]
    [InlineData(PantsEngineHealth.Corrupt, PantsStorageVerificationOutcomeKind.Corruption)]
    public void ShouldClassifyReportByHealth(
        PantsEngineHealth health,
        PantsStorageVerificationOutcomeKind expected)
    {
        var report = new PantsStorageVerificationReport(
            0, 0, 0, 0, 0, null, 0, 0, 0, true, health, []);

        var outcome = PantsStorageVerificationOutcome.FromReport(report);

        Assert.Equal(expected, outcome.Kind);
        Assert.Equal((int)expected, outcome.ExitCode);
        Assert.Same(report, outcome.Report);
    }

    [Theory]
    [InlineData(PantsErrorCode.Busy)]
    [InlineData(PantsErrorCode.Timeout)]
    [InlineData(PantsErrorCode.WriteStall)]
    [InlineData(PantsErrorCode.NoSpace)]
    public void ShouldReportDegradedWhenVerificationFailsFromTransientBackpressure(PantsErrorCode code)
    {
        Assert.Equal(PantsStorageVerificationOutcomeKind.Degraded, code.GetVerificationOutcomeKind());
    }

    [Theory]
    [InlineData(PantsErrorCode.ResourceLimit)]
    [InlineData(PantsErrorCode.Internal)]
    public void ShouldReportInternalWhenVerificationHitsADefect(PantsErrorCode code)
    {
        Assert.Equal(PantsStorageVerificationOutcomeKind.Internal, code.GetVerificationOutcomeKind());
    }

    [Theory]
    [InlineData(PantsErrorCode.NotFound)]
    [InlineData(PantsErrorCode.InvalidPath)]
    public void ShouldReportStorageWhenRequiredVerificationDataIsMissing(PantsErrorCode code)
    {
        Assert.Equal(PantsStorageVerificationOutcomeKind.Storage, code.GetVerificationOutcomeKind());
    }

    [Theory]
    [InlineData(PantsErrorCode.Fenced)]
    [InlineData(PantsErrorCode.LeaseIndeterminate)]
    [InlineData(PantsErrorCode.LeaseEpochExhausted)]
    [InlineData(PantsErrorCode.Io)]
    [InlineData(PantsErrorCode.LeaseHeld)]
    [InlineData(PantsErrorCode.LeaseUnavailable)]
    [InlineData(PantsErrorCode.Aborted)]
    public void ShouldReportStorageWhenVerificationFailsFromFencingOrTransientFault(PantsErrorCode code)
    {
        Assert.Equal(PantsStorageVerificationOutcomeKind.Storage, code.GetVerificationOutcomeKind());
    }

    [Theory]
    [InlineData(PantsErrorCode.Corruption)]
    [InlineData(PantsErrorCode.RecoveryFailed)]
    [InlineData(PantsErrorCode.CompatibilityError)]
    public void ShouldStillReportCorruptionWhenVerificationFindsUnrecoverableState(PantsErrorCode code)
    {
        Assert.Equal(PantsStorageVerificationOutcomeKind.Corruption, code.GetVerificationOutcomeKind());
    }

    [Theory]
    [InlineData(PantsErrorCode.InvalidArgument)]
    [InlineData(PantsErrorCode.NotSupported)]
    [InlineData(PantsErrorCode.MemoryModeViolation)]
    [InlineData(PantsErrorCode.WriteConflict)]
    public void ShouldReportUsageWhenVerificationRejectsTheRequest(PantsErrorCode code)
    {
        Assert.Equal(PantsStorageVerificationOutcomeKind.Usage, code.GetVerificationOutcomeKind());
    }

    [Fact]
    public void ShouldCarryCodeAndMessageGivenOnlineVerificationException()
    {
        var exception = new PantsFencedException("another leader took over");

        var outcome = PantsStorageVerificationOutcome.FromException(exception);

        Assert.Equal(PantsStorageVerificationOutcomeKind.Storage, outcome.Kind);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(PantsErrorCode.Fenced, outcome.ErrorCode);
        Assert.Equal("another leader took over", outcome.Message);
        Assert.Null(outcome.Report);
        Assert.Null(outcome.PathFailure);
    }

    static bool CanStillEnumerate(string path)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool CanStillRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    static async Task WriteEmptyFixtureAsync(string path)
    {
        await File.WriteAllTextAsync(
            Path.Combine(path, "FORMAT"),
            "midge-format-version=3\n");
        await File.WriteAllTextAsync(
            Path.Combine(path, "manifest.json"),
            """
            {
              "last_persisted_sequence": 0,
              "files": [],
              "column_families": [],
              "next_wal_seq": 1,
              "next_sst_seqs": {},
              "edit_checkpoint_id": 0
            }
            """);
        await File.WriteAllBytesAsync(Path.Combine(path, "manifest.journal"), []);
    }
}
