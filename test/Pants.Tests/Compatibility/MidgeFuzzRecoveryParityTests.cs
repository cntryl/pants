using System.Text.Json;
using Cntryl.Pants.Exceptions;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeFuzzRecoveryParityTests
{
    const string FixtureDirectory =
        "Fixtures/Compatibility/Midge/445a901d8c34ec6a1fca91038da6bb187caaa7b6/fuzz";
    const int RepeatedFailureCount = 64;
    static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldReleaseSeedLeaseBeforeReturningToFuzzInputMutation()
    {
        using var directory = new TemporaryDirectory();

        await SeedDatabaseAsync(directory.Path);
        await using var successor = await OpenAsync(directory.Path);
        await successor.ShutdownAsync(ShutdownTimeout);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "probe"), "bounded fixture");
    }

    [Fact]
    public async Task ShouldFinishRecoveryAttemptsBeforeReturningToTheNextFuzzIteration()
    {
        using var directory = new TemporaryDirectory();
        await SeedDatabaseAsync(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "probe"), "bounded fixture");

        await ExerciseOpenAndVerifyAsync(directory.Path);

        await using var successor = await OpenAsync(directory.Path);
        await successor.ShutdownAsync(ShutdownTimeout);
    }

    [Fact]
    public async Task ShouldCompleteRecoveryWhenReplayingThePreservedIntentTimeoutInput()
    {
        using var directory = new TemporaryDirectory();
        await SeedDatabaseAsync(directory.Path);
        var intentPath = Path.Combine(directory.Path, "intent_log.json");
        var validIntent = await File.ReadAllBytesAsync(intentPath);
        await File.WriteAllBytesAsync(intentPath, ReadFixture("intent_timeout_18.bin"));

        var verification = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
        var recovery = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, verification.Code);
        Assert.Equal(PantsErrorCode.RecoveryFailed, recovery.Code);
        Assert.Equal(ReadFixture("intent_timeout_18.bin"), await File.ReadAllBytesAsync(intentPath));

        await File.WriteAllBytesAsync(intentPath, validIntent);
        await using var successor = await OpenAsync(directory.Path);
        await successor.ShutdownAsync(ShutdownTimeout);
    }

    [Fact]
    public async Task ShouldReleaseStartupWorkersWhenReplayingRepeatedOneByteIntentFailures()
    {
        for (var iteration = 0; iteration < RepeatedFailureCount; iteration++)
        {
            using var directory = new TemporaryDirectory();
            await SeedDatabaseAsync(directory.Path);
            var intentPath = Path.Combine(directory.Path, "intent_log.json");
            await File.WriteAllBytesAsync(intentPath, ReadFixture("intent_timeout_1.bin"));

            var verification = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
                PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
            var strict = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
                OpenAsync(directory.Path).AsTask());
            Assert.Equal(PantsErrorCode.RecoveryFailed, verification.Code);
            Assert.Equal(PantsErrorCode.RecoveryFailed, strict.Code);

            await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
                OpenAsync(directory.Path).AsTask());
            await using (var salvaged = await OpenAsync(directory.Path, PantsRecoveryPolicy.Salvage))
            {
                var metrics = await salvaged.Diagnostics.GetRuntimeMetricsAsync();
                Assert.Equal(PantsEngineHealth.SalvageMode, metrics.Health);
            }

            Assert.NotEmpty(Directory.GetFiles(directory.Path, "intent_log.json.salvage-retained*"));
            using (var intent = JsonDocument.Parse(await File.ReadAllBytesAsync(intentPath)))
            {
                Assert.Empty(intent.RootElement.EnumerateArray());
            }

            await using var successor = await OpenAsync(directory.Path);
            await successor.ShutdownAsync(ShutdownTimeout);
        }
    }

    static async Task SeedDatabaseAsync(string path)
    {
        await using var database = await OpenAsync(path);
        await database.ShutdownAsync(ShutdownTimeout);
    }

    static async Task ExerciseOpenAndVerifyAsync(string path)
    {
        _ = await PantsDatabase.VerifyPathAsync(path);
        await using (var strict = await OpenAsync(path))
        {
            await strict.ShutdownAsync(ShutdownTimeout);
        }

        await using var salvage = await OpenAsync(path, PantsRecoveryPolicy.Salvage);
        await salvage.ShutdownAsync(ShutdownTimeout);
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        PantsRecoveryPolicy recoveryPolicy = PantsRecoveryPolicy.Strict) =>
        PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path)
                .WithRecoveryPolicy(recoveryPolicy)
                .WithBackgroundCompaction(false));

    static byte[] ReadFixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, FixtureDirectory, fileName));
}
