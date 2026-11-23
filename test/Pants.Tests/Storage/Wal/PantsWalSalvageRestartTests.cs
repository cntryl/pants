using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsWalSalvageRestartTests
{
    [Fact]
    public async Task ShouldResumeSalvageGivenInterruptionAfterActiveWalQuarantine()
    {
        using var directory = new TemporaryDirectory();
        ulong discardedHighWater;
        await using (var seed = await OpenAsync(directory.Path, PantsRecoveryPolicy.Strict))
        {
            for (var index = 0; index < 4; index++)
            {
                await using var transaction = await seed.Transactions.BeginAsync(
                    seed.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(Key(index), Value(index));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            discardedHighWater =
                (await seed.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
        }

        var walPath = WalTestFiles.ActivePath(directory.Path);
        var walBytes = await File.ReadAllBytesAsync(walPath);
        var corruptOffset = WalSegmentCodec.HeaderSize;

        for (var index = 0; index < 2; index++)
        {
            corruptOffset += sizeof(uint) * 2 + checked((int)BitConverter.ToUInt32(walBytes, corruptOffset));
        }


        walBytes[corruptOffset + sizeof(uint)] ^= 0xff;
        await File.WriteAllBytesAsync(walPath, walBytes);

        var failpoints = new ArmableFailpointHandler();
        failpoints.Arm(
            Failpoint.AfterActiveWalSalvageQuarantine,
            static _ => new PantsIOException("Interrupted after active WAL quarantine."));
        await Assert.ThrowsAsync<PantsIOException>(() =>
            OpenAsync(directory.Path, PantsRecoveryPolicy.Salvage, failpoints).AsTask());

        var walDirectory = Path.Combine(directory.Path, "wal");
        var interruptedLedger = WalLedgerStore.LoadExisting(walDirectory);
        Assert.False(File.Exists(interruptedLedger.ActivePath));
        Assert.True(File.Exists($"{interruptedLedger.ActivePath}.salvage-retained"));
        Assert.True(interruptedLedger.ReservedThrough >= discardedHighWater);

        var replacementFailpoints = new ArmableFailpointHandler();
        replacementFailpoints.Arm(
            Failpoint.AfterActiveWalSalvageReplacementHeader,
            static _ => new PantsIOException("Interrupted after replacement WAL header creation."));
        await Assert.ThrowsAsync<PantsIOException>(() =>
            OpenAsync(directory.Path, PantsRecoveryPolicy.Salvage, replacementFailpoints).AsTask());

        var headerInterruptedLedger = WalLedgerStore.LoadExisting(walDirectory);
        Assert.Equal(interruptedLedger.Active.Open.Id, headerInterruptedLedger.Active.Open.Id);
        Assert.False(File.Exists(headerInterruptedLedger.ActivePath));
        var replacementPath = Path.Combine(
            walDirectory,
            $"{headerInterruptedLedger.NextSegmentId:00000000000000000000}.wal");
        Assert.Equal(WalSegmentCodec.HeaderSize, new FileInfo(replacementPath).Length);
        Assert.True(headerInterruptedLedger.ReservedThrough >= discardedHighWater);

        await using (var salvaged = await OpenAsync(directory.Path, PantsRecoveryPolicy.Salvage))
        {
            Assert.Equal(
                PantsEngineHealth.SalvageMode,
                (await salvaged.Diagnostics.GetRuntimeMetricsAsync()).Health);
            Assert.Equal("value-0", await ReadAsync(salvaged, Key(0)));
            Assert.Equal("value-1", await ReadAsync(salvaged, Key(1)));
            Assert.Null(await ReadAsync(salvaged, Key(2)));
            Assert.Null(await ReadAsync(salvaged, Key(3)));

            await using var transaction = await salvaged.Transactions.BeginAsync(
                salvaged.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("after-restart"u8.ToArray(), "durable"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            Assert.True(
                (await salvaged.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence > discardedHighWater);
        }

        await using var strict = await OpenAsync(directory.Path, PantsRecoveryPolicy.Strict);
        Assert.Equal("value-0", await ReadAsync(strict, Key(0)));
        Assert.Equal("value-1", await ReadAsync(strict, Key(1)));
        Assert.Null(await ReadAsync(strict, Key(2)));
        Assert.Null(await ReadAsync(strict, Key(3)));
        Assert.Equal("durable", await ReadTextAsync(strict, "after-restart"u8.ToArray()));
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        PantsRecoveryPolicy recoveryPolicy,
        IFailpointHandler? failpoints = null
    ) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(path)
                .WithBackgroundCompaction(false)
                .WithRecoveryPolicy(recoveryPolicy),
            new RuntimeDependencies(failpoints));

    static byte[] Key(int index) => TestBytes.FromString($"key-{index}");

    static byte[] Value(int index) => TestBytes.FromString($"value-{index}");

    static async Task<string?> ReadAsync(IPantsDatabase database, byte[] key) =>
        await ReadTextAsync(database, key);

    static async Task<string?> ReadTextAsync(IPantsDatabase database, byte[] key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(key);
        return value is null ? null : TestBytes.ToText(value.Value);
    }
}
