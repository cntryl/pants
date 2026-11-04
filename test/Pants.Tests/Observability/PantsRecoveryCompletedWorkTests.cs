using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsRecoveryCompletedWorkTests
{
    [Fact]
    public async Task ShouldReportTheVerifiedWalPrefixWhenAnIncompleteActiveTailIsTolerated()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using var write = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            write.Put("key"u8.ToArray(), "value"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.Sync);
        }
        using (var tail = new FileStream(WalTestFiles.ActivePath(directory.Path), FileMode.Append))
        {
            tail.WriteByte(1);
        }
        var completed = new List<RecoveryWorkCompleted>();

        await using var recovered = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(recoveryWorkCompleted: completed.Add));
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
        Assert.Equal(1, completed.Where(static work => work.Stage == "wal_replay").Sum(static work => work.Frames));
    }

    [Fact]
    public async Task ShouldReportCompletedRecoveryWorkWhileRecoveringExactWalValuesAndFamilyFrontiers()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.ColumnFamilies.CreateAsync("records");
            for (var index = 0; index < 32; index++)
            {
                await using var transaction = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
                transaction.Put(TestBytes.FromString($"key-{index}"), TestBytes.FromString($"value-{index}"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }
        }

        using var measurements = new RuntimeMeterMeasurements(new HashSet<string>(StringComparer.Ordinal)
        {
            "pants.recovery.completed_bytes",
            "pants.recovery.completed_frames",
            "pants.recovery.completed_operations"
        });
        var observedTags = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscription) =>
            {
                if (instrument.Meter.Name == PantsDiagnostics.Meter.Name &&
                    instrument.Name.StartsWith("pants.recovery.completed_", StringComparison.Ordinal))
                {
                    subscription.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => observedTags.Enqueue(tags.ToArray()));
        listener.Start();
        await using var recovered = await PantsDatabase.OpenAsync(options);
        var recoveredFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(await recovered.ColumnFamilies.GetAsync("records"));
        await using var reader = await recovered.Transactions.BeginAsync(recoveredFamily, PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 32; index++)
        {
            Assert.Equal(TestBytes.FromString($"value-{index}"),
                (await reader.GetAsync(TestBytes.FromString($"key-{index}")))?.ToArray());
        }

        Assert.True(measurements["pants.recovery.completed_bytes"] > 0);
        Assert.True(measurements["pants.recovery.completed_frames"] > 0);
        Assert.True(measurements["pants.recovery.completed_operations"] >= 2);
        Assert.NotEmpty(observedTags);
        Assert.All(observedTags, tags => Assert.Equal("pants.recovery.stage", Assert.Single(tags).Key));
    }
}
