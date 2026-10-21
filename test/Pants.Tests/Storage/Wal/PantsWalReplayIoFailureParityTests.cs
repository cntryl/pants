using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsWalReplayIoFailureParityTests
{
    const int AcknowledgedWriteCount = 12;

    [Fact]
    public async Task ShouldKeepAcknowledgedWritesWhenSalvageReplayHitsATransientIoError()
    {
        using var baseline = new TemporaryDirectory();
        var options = CreateOptions(baseline.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < AcknowledgedWriteCount; index++)
            {
                await using var transaction = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(Key(index), Value());
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using (var control = await PantsDatabase.OpenAsync(options))
        {
            await AssertAcknowledgedWritesAsync(control);
            await control.ShutdownAsync(TestTimeouts.Expected);
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(baseline.Path, "sst"), "*.sst"));

        var exercisedReadFailures = 0;
        const int MaximumReadFailures = 256;
        for (var failAt = 1; failAt <= MaximumReadFailures; failAt++)
        {
            using var trial = new TemporaryDirectory();
            CopyDirectory(baseline.Path, trial.Path);
            var failpoints = new NthWalReplayReadFailureHandler(failAt);
            IPantsDatabase? faultedOpen = null;
            Exception? openFailure = null;
            try
            {
                faultedOpen = await PantsDatabase.OpenForTestingAsync(
                    CreateOptions(trial.Path),
                    new RuntimeDependencies(failpoints));
            }
            catch (Exception exception)
            {
                openFailure = exception;
            }

            if (!failpoints.Fired)
            {
                if (faultedOpen is not null)
                {
                    await faultedOpen.ShutdownAsync(TestTimeouts.Expected);
                }

                Assert.Null(openFailure);
                break;
            }

            exercisedReadFailures++;
            if (faultedOpen is not null)
            {
                await faultedOpen.ShutdownAsync(TestTimeouts.Expected);
            }

            if (openFailure is not null)
            {
                Assert.Contains(
                    ExceptionChain(openFailure),
                    static exception => exception is IOException ioException &&
                        ioException.Message == NthWalReplayReadFailureHandler.ErrorMessage);
            }

            await using var recovered = await PantsDatabase.OpenAsync(CreateOptions(trial.Path));
            await AssertAcknowledgedWritesAsync(recovered);
        }

        Assert.InRange(exercisedReadFailures, 1, MaximumReadFailures - 1);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithRecoveryPolicy(PantsRecoveryPolicy.Salvage);

    static byte[] Key(int index) =>
        System.Text.Encoding.UTF8.GetBytes($"acked-{index:000}");

    static byte[] Value() => "v"u8.ToArray();

    static async Task AssertAcknowledgedWritesAsync(IPantsDatabase database)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < AcknowledgedWriteCount; index++)
        {
            Assert.Equal(
                Value(),
                (await reader.GetAsync(Key(index)))!.Value.ToArray());
        }
    }

    static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copyPath = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
            File.Copy(file, copyPath);
        }
    }

    sealed class NthWalReplayReadFailureHandler(int failAt) : IFailpointHandler
    {
        public const string ErrorMessage = "Injected transient WAL replay read failure.";

        int _readCount;
        int _fired;

        public bool Fired => Volatile.Read(ref _fired) != 0;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeWalReplayRead ||
                Interlocked.Increment(ref _readCount) != failAt)
            {
                return;
            }

            Volatile.Write(ref _fired, 1);
            throw new IOException(ErrorMessage);
        }
    }
}
