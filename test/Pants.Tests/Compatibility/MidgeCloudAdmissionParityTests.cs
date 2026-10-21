using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeCloudAdmissionParityTests
{
    const long OneMebibyte = 1024L * 1024;
    const long ManyFamilyBudget = 256L * 1024;
    const long SmallMemoryBudget = 8L * 1024 * 1024;
    const int FamilyCount = 160;
    const int RepeatedFailureCount = 3;
    static readonly string[] WorkingDirectoryNames = ["wal", "sst", "staging"];

    [Fact]
    public async Task ShouldKeepOrdinaryCloudCommitsFlushableWhenLocalBudgetIsSmallerThanMemtableLimit()
    {
        foreach (var (valueLengths, spillPoolBytes) in new (int[] ValueLengths, long? SpillPoolBytes)[]
                 {
                     ([96, 96, 96, 96], null),
                     ([40, 96, 96, 96], null),
                     ([40, 96, 96, 96], 1)
                 })
        {
            using var directory = new TemporaryDirectory();
            var options = CreateCloudOptions(directory.Path, OneMebibyte)
                .WithMemtableLimits(8 * OneMebibyte);
            if (spillPoolBytes is { } poolBytes)
            {
                options = options.WithTransactionMemoryPool(poolBytes);
            }

            var familyName = "data";
            var values = new byte[valueLengths.Length][];
            var seed = 0x9e37_79b9u;
            await using (var database = await PantsDatabase.OpenAsync(options))
            {
                var family = await database.ColumnFamilies.CreateAsync(familyName);
                for (var index = 0; index < valueLengths.Length; index++)
                {
                    values[index] = CreateXorShiftBytes(valueLengths[index] * 1024, ref seed);
                    await using var transaction = await database.Transactions.BeginAsync(
                        family,
                        PantsTransactionMode.ReadWrite);
                    transaction.Put(UInt32Bytes(checked((uint)index)), values[index]);
                    await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
                }

                await database.Maintenance.FlushAsync(family);
                await database.ShutdownAsync(TestTimeouts.Expected);
            }

            await using var reopened = await PantsDatabase.OpenAsync(options);
            var reopenedFamily = await reopened.ColumnFamilies.GetAsync(familyName);
            Assert.NotNull(reopenedFamily);
            await using var reader = await reopened.Transactions.BeginAsync(
                reopenedFamily,
                PantsTransactionMode.ReadOnly);
            for (var index = 0; index < values.Length; index++)
            {
                Assert.Equal(
                    values[index],
                    (await reader.GetAsync(UInt32Bytes(checked((uint)index))))?.ToArray());
            }
        }
    }

    [Fact]
    public async Task ShouldRejectAtomicCloudWorkBeforeWalAppendWhenItsFlushCannotFit()
    {
        foreach (var (valueLengths, spillPoolBytes) in new (int[] ValueLengths, long? SpillPoolBytes)[]
                 {
                     ([160 * 1024], null),
                     ([96 * 1024, 96 * 1024], null),
                     ([96 * 1024, 96 * 1024], 1)
                 })
        {
            using var directory = new TemporaryDirectory();
            var options = CreateCloudOptions(directory.Path, OneMebibyte);
            if (spillPoolBytes is { } poolBytes)
            {
                options = options.WithTransactionMemoryPool(poolBytes);
            }

            await using var database = await PantsDatabase.OpenAsync(options);
            var family = await database.ColumnFamilies.CreateAsync("data");
            var seed = 0x9e37_79b9u;
            var values = valueLengths.Select(length => CreateXorShiftBytes(length, ref seed)).ToArray();
            var walBytesBefore = WalBytes(directory.Path);

            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < values.Length; index++)
                {
                    transaction.Put(UInt32Bytes(checked((uint)index)), values[index]);
                }

                if (spillPoolBytes is not null)
                {
                    Assert.NotEmpty(Directory.GetFiles(
                        Path.Combine(directory.Path, "txn"),
                        "*.run"));
                }

                await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                    transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
            }

            Assert.Equal(walBytesBefore, WalBytes(directory.Path));
            await using var reader = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadOnly);
            for (var index = 0; index < values.Length; index++)
            {
                Assert.Null(await reader.GetAsync(UInt32Bytes(checked((uint)index))));
            }
        }
    }

    [Fact]
    public async Task ShouldRejectOversizedCloudTombstonesBeforeWalAppend()
    {
        foreach (var range in new[] { false, true })
        {
            using var directory = new TemporaryDirectory();
            await using var database = await PantsDatabase.OpenAsync(
                CreateCloudOptions(directory.Path, OneMebibyte));
            var family = await database.ColumnFamilies.CreateAsync("data");
            await using var transaction = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            if (range)
            {
                transaction.DeleteRange(
                    Enumerable.Repeat((byte)'a', 24 * 1024).ToArray(),
                    Enumerable.Repeat((byte)'z', 24 * 1024).ToArray());
            }
            else
            {
                transaction.Delete(Enumerable.Repeat((byte)'a', 32 * 1024).ToArray());
            }

            var walBytesBefore = WalBytes(directory.Path);

            await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());

            Assert.Equal(walBytesBefore, WalBytes(directory.Path));
        }
    }

    [Fact]
    public async Task ShouldFlushManySmallCloudFamiliesWithoutExhaustingSharedWalStaging()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateCloudOptions(directory.Path, ManyFamilyBudget)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(32L * 1024 * 1024));
        var values = new byte[FamilyCount][];
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var families = new List<IPantsColumnFamily>(FamilyCount);
            for (var index = 0; index < FamilyCount; index++)
            {
                families.Add(await database.ColumnFamilies.CreateAsync($"cf-{index}"));
            }

            var seed = 0x1f37_9945u;
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = CreateXorShiftBytes(2048, ref seed);
            }

            var walBytesBeforeOversized = WalBytes(directory.Path);
            await using (var oversized = await database.Transactions.BeginAsync(
                             families[0],
                             PantsTransactionMode.ReadWrite))
            {
                oversized.Put("too-large"u8.ToArray(), new byte[64 * 1024]);
                await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                    oversized.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
            }

            Assert.Equal(walBytesBeforeOversized, WalBytes(directory.Path));

            for (var index = 0; index < families.Count; index++)
            {
                var committed = false;
                for (var attempt = 0; attempt < RepeatedFailureCount && !committed; attempt++)
                {
                    try
                    {
                        await using var transaction = await database.Transactions.BeginAsync(
                            families[index],
                            PantsTransactionMode.ReadWrite);
                        transaction.Put("key"u8.ToArray(), values[index]);
                        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
                        committed = true;
                    }
                    catch (Exception exception) when (
                        exception is PantsNoSpaceException or PantsWriteStallException)
                    {
                        foreach (var accepted in families.Take(index))
                        {
                            await database.Maintenance.FlushAsync(accepted);
                        }
                    }
                }

                Assert.True(committed, $"Family {index} must progress after flushing accepted work.");
                Assert.InRange(WorkingBytes(directory.Path), 0, ManyFamilyBudget);
            }

            foreach (var family in families)
            {
                await database.Maintenance.FlushAsync(family);
            }

            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < FamilyCount; index++)
        {
            var family = await reopened.ColumnFamilies.GetAsync($"cf-{index}");
            Assert.NotNull(family);
            await using var reader = await reopened.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadOnly);
            Assert.Equal(values[index], (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
        }

        Assert.InRange(WorkingBytes(directory.Path), 0, ManyFamilyBudget);
        Assert.True(RemoteSstBytes(directory.Path) > ManyFamilyBudget);
        await reopened.ShutdownAsync(TestTimeouts.Expected);
    }

    [Fact]
    public async Task ShouldReadColdCloudSstWhenDefaultMemoryBudgetIsEightMebibytes()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var databasePath = Path.Combine(directory.Path, string.Concat(Enumerable.Repeat("nested-cache-", 12)));
        var location = new PantsCloudStorageLocation(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "midge-cloud-admission/");
        var options = PantsOpenOptions.Cloud(databasePath, location)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(SmallMemoryBudget));
        var pools = PantsOpenOptionsResolver.Resolve(options);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        await using (var seed = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            var family = await seed.ColumnFamilies.CreateAsync("data");
            await using var transaction = await seed.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
            await seed.Maintenance.FlushAsync(family);
            await seed.ShutdownAsync(TestTimeouts.Expected);
        }

        foreach (var localSst in Directory.GetFiles(
                     Path.Combine(databasePath, "sst"),
                     "*.sst",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(localSst);
        }

        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        var familyAfterReopen = await reopened.ColumnFamilies.GetAsync("data");
        Assert.NotNull(familyAfterReopen);
        await using (var reader = await reopened.Transactions.BeginAsync(
                         familyAfterReopen,
                         PantsTransactionMode.ReadOnly))
        {
            var actual = await reader.GetAsync("key"u8.ToArray());
            var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();

            Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(actual)));
            Assert.True(metrics.RemoteRangeRequestsTotal > 0);
            Assert.True(pools.BlockCacheBytes > 0);
            var publicPools = checked(
                2 * pools.MemtableSizeLimitBytes +
                pools.TransactionMemoryPoolBytes +
                pools.BlockCacheBytes);
            Assert.True(publicPools <= SmallMemoryBudget);
        }

        await reopened.ShutdownAsync(TestTimeouts.Expected);
    }

    static PantsOpenOptions CreateCloudOptions(string path, long? localBudgetBytes = null)
    {
        var options = PantsOpenOptions
            .SimulatedCloud(path, "pants-tests", "midge-cloud-admission/")
            .WithBackgroundCompaction(false);
        return localBudgetBytes is { } budget
            ? options.WithSimulatedCloudLocalStorageBudget(budget)
            : options;
    }

    static byte[] CreateXorShiftBytes(int length, ref uint seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            bytes[index] = (byte)seed;
        }

        return bytes;
    }

    static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    static long WalBytes(string root) => FileBytes(Path.Combine(root, "wal"));

    static long WorkingBytes(string root) =>
        WorkingDirectoryNames
            .Sum(name => FileBytes(Path.Combine(root, name)));

    static long RemoteSstBytes(string root) => FileBytes(Path.Combine(root, "cloud_store", "sst"));

    static long FileBytes(string path) => !Directory.Exists(path)
        ? 0
        : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(static file => new FileInfo(file).Length);
}
