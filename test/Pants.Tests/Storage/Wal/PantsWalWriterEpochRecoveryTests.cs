using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsWalWriterEpochRecoveryTests
{
    [Theory]
    [InlineData(PantsRecoveryPolicy.Strict)]
    [InlineData(PantsRecoveryPolicy.Salvage)]
    public async Task ShouldSkipLateLowerEpochMutationGivenNewerWriterAlreadyAppeared(
        PantsRecoveryPolicy recoveryPolicy)
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalAsync(
            directory.Path,
            CreatePut("legacy", "preserved", 1, 1),
            CreatePut("target", "fresh", 10, 2),
            CreatePut("target", "stale", 2, 1));

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        await using var database = await OpenAsync(directory.Path, recoveryPolicy);

        Assert.Equal(10, report.WalBoundary);
        Assert.Equal(2, report.WalRecoveryRecordsReplayed);
        Assert.Equal("preserved", await ReadAsync(database, "legacy"));
        Assert.Equal("fresh", await ReadAsync(database, "target"));
        Assert.Equal(PantsEngineHealth.Healthy, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        Assert.Equal(2, (await database.Diagnostics.GetRecoveryMetricsAsync()).WalRecordsReplayed);
    }

    [Fact]
    public async Task ShouldSkipOverlappingLowerEpochMutationGivenNewerWriterAppearsLater()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalAsync(
            directory.Path,
            CreatePut("stale-first", "stale", 3, 1),
            CreatePut("fresh-second", "fresh", 2, 2));

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        await using var database = await OpenAsync(directory.Path, PantsRecoveryPolicy.Strict);

        Assert.Equal(2, report.WalBoundary);
        Assert.Equal(1, report.WalRecoveryRecordsReplayed);
        Assert.Null(await ReadAsync(database, "stale-first"));
        Assert.Equal("fresh", await ReadAsync(database, "fresh-second"));
    }

    [Fact]
    public async Task ShouldCarryWriterEpochFrontierAcrossSealedAndActiveWal()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalFileAsync(
            directory.Path,
            "00000000000000000001.wal",
            CreatePut("stale-sealed", "stale", 3, 1));
        await WriteWalAsync(
            directory.Path,
            CreatePut("fresh-active", "fresh", 2, 2));

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        await using var database = await OpenAsync(directory.Path, PantsRecoveryPolicy.Strict);

        Assert.Equal(2, report.WalBoundary);
        Assert.Equal(1, report.WalRecoveryRecordsReplayed);
        Assert.Null(await ReadAsync(database, "stale-sealed"));
        Assert.Equal("fresh", await ReadAsync(database, "fresh-active"));
    }

    [Fact]
    public async Task ShouldFailStrictRecoveryGivenStaleTransactionBatchIsCorrupt()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        var staleBatch = WalCodec.EncodeTransactionBatch(
            7,
            10,
            1,
            [CreateMutation("stale", "hidden", 0)]);
        var batchMagicOffset = staleBatch.AsSpan().IndexOf("TB"u8);
        Assert.True(batchMagicOffset >= 0);
        staleBatch[batchMagicOffset] = (byte)'X';
        await WriteWalAsync(
            directory.Path,
            staleBatch,
            CreatePut("fresh", "visible", 10, 2));

        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldFailStrictRecoveryGivenStaleTransactionBeginIsDuplicated()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        const ulong transactionId = 7;
        await WriteWalAsync(
            directory.Path,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                transactionId,
                10,
                1),
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                transactionId,
                11,
                1),
            CreatePut("fresh", "visible", 10, 2));

        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldIsolateReusedTransactionIdGivenWriterEpochChanges()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        const ulong transactionId = 1;
        await WriteWalAsync(
            directory.Path,
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                transactionId,
                1,
                11),
            WalCodec.EncodeTransactionMutation(
                CreateMutation("orphaned", "hidden", 2),
                transactionId,
                11),
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionBegin,
                transactionId,
                3,
                12),
            WalCodec.EncodeTransactionMutation(
                CreateMutation("committed", "visible", 4),
                transactionId,
                12),
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionCommit,
                transactionId,
                5,
                12),
            WalCodec.EncodeTransactionMarker(
                WalOperation.TransactionCommit,
                transactionId,
                6,
                11));

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        await using var database = await OpenAsync(directory.Path, PantsRecoveryPolicy.Strict);

        Assert.Equal(5, report.WalBoundary);
        Assert.Equal(5, report.WalRecoveryRecordsReplayed);
        Assert.Null(await ReadAsync(database, "orphaned"));
        Assert.Equal("visible", await ReadAsync(database, "committed"));
    }

    [Fact]
    public async Task ShouldSalvageFreshMixedEpochPrefixGivenCorruptTail()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalWithCorruptTailAsync(
            directory.Path,
            CreatePut("legacy", "preserved", 1, 1),
            CreatePut("target", "fresh", 10, 2),
            CreatePut("target", "stale", 2, 1));

        var strict =
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());
        Assert.Equal(PantsErrorCode.RecoveryFailed, strict.Code);

        await using var salvaged = await OpenAsync(directory.Path, PantsRecoveryPolicy.Salvage);

        Assert.Equal("preserved", await ReadAsync(salvaged, "legacy"));
        Assert.Equal("fresh", await ReadAsync(salvaged, "target"));
        Assert.Equal(PantsEngineHealth.SalvageMode, (await salvaged.Diagnostics.GetRuntimeMetricsAsync()).Health);
        Assert.Equal(2, (await salvaged.Diagnostics.GetRecoveryMetricsAsync()).WalRecordsReplayed);
        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(directory.Path, "wal"),
            "*.salvage-retained*"));
    }

    [Fact]
    public async Task ShouldFailStrictRecoveryGivenCorruptedLengthHidesValidActiveWalSuffix()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalWithCorruptedLengthBeforeValidSuffixAsync(directory.Path);

        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldSalvageVerifiedPrefixGivenCorruptedLengthHidesValidActiveWalSuffix()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalWithCorruptedLengthBeforeValidSuffixAsync(directory.Path);

        await using var database = await OpenAsync(directory.Path, PantsRecoveryPolicy.Salvage);

        Assert.Equal("preserved", await ReadAsync(database, "prefix"));
        Assert.Null(await ReadAsync(database, "corrupt-length"));
        Assert.Null(await ReadAsync(database, "hidden-suffix"));
        Assert.Equal(PantsEngineHealth.SalvageMode, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(directory.Path, "wal"),
            "*.salvage-retained*"));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("conflicting")]
    public async Task ShouldFailStrictRecoveryGivenDuplicateLogicalVersionAcrossStandaloneAndBatch(
        string duplicateValue)
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalWithDuplicateLogicalVersionAsync(directory.Path, duplicateValue);

        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("conflicting")]
    public async Task ShouldSalvagePrefixGivenDuplicateLogicalVersionAcrossStandaloneAndBatch(
        string duplicateValue)
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalWithDuplicateLogicalVersionAsync(directory.Path, duplicateValue);

        await using var database = await OpenAsync(directory.Path, PantsRecoveryPolicy.Salvage);

        Assert.Equal("original", await ReadAsync(database, "duplicate"));
        Assert.Equal(PantsEngineHealth.SalvageMode, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(directory.Path, "wal"),
            "*.salvage-retained*"));
    }

    [Fact]
    public async Task ShouldFailStrictRecoveryGivenDuplicatePointDeleteLogicalVersion()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalAsync(
            directory.Path,
            CreateDelete("duplicate", 7, 1),
            WalCodec.EncodeTransactionBatch(
                9,
                6,
                1,
                [CreateDeleteMutation("duplicate", 0)]));

        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectDuplicateLogicalVersionGivenOfflineVerification()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalWithDuplicateLogicalVersionAsync(directory.Path, "conflicting");

        var exception =
            await Assert.ThrowsAsync<PantsCorruptionException>(() =>
                PantsDatabase.VerifyPathAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectDuplicateLogicalVersionGivenOnlineVerification()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "wal", "00000000000000000001.wal"),
            CreateDuplicateLogicalVersionWal("conflicting"));

        var exception =
            await Assert.ThrowsAsync<PantsCorruptionException>(() =>
                database.PersistentStorage!.VerifyAsync(TimeSpan.FromSeconds(2)).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Theory]
    [InlineData(PantsRecoveryPolicy.Strict)]
    [InlineData(PantsRecoveryPolicy.Salvage)]
    public async Task ShouldTreatDuplicateLogicalVersionAsCorruptionBelowPersistedFrontier(
        PantsRecoveryPolicy recoveryPolicy)
    {
        using var directory = new TemporaryDirectory();
        await SeedPersistedFrontierAsync(directory.Path);
        DeleteSealedWalSegments(directory.Path);
        await WriteWalWithDuplicateLogicalVersionAsync(directory.Path, "conflicting");

        await AssertDuplicateRecoveryAsync(directory.Path, recoveryPolicy);
    }

    [Theory]
    [InlineData(PantsRecoveryPolicy.Strict, false)]
    [InlineData(PantsRecoveryPolicy.Salvage, false)]
    [InlineData(PantsRecoveryPolicy.Strict, true)]
    [InlineData(PantsRecoveryPolicy.Salvage, true)]
    public async Task ShouldTreatDuplicateLogicalVersionAsCorruptionGivenFamilyIsNotActive(
        PantsRecoveryPolicy recoveryPolicy,
        bool droppedFamily)
    {
        using var directory = new TemporaryDirectory();
        uint columnFamilyId;
        if (droppedFamily)
        {
            columnFamilyId = await InitializeWithDroppedFamilyAsync(directory.Path);
        }
        else
        {
            await InitializeDatabaseAsync(directory.Path);
            columnFamilyId = 999;
        }

        DeleteSealedWalSegments(directory.Path);
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "wal", "wal.log"),
            CreateDuplicateLogicalVersionWal("conflicting", columnFamilyId));

        await AssertDuplicateRecoveryAsync(directory.Path, recoveryPolicy);
    }

    [Theory]
    [InlineData(10, 2, 10)]
    [InlineData(7, 7, 7)]
    public async Task ShouldReportMaximumSequenceAndReopenGivenDistinctKeysAreNotOrdered(
        int firstSequence,
        int secondSequence,
        long expectedBoundary)
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalAsync(
            directory.Path,
            CreatePut("first", "one", checked((ulong)firstSequence), 1),
            CreatePut("second", "two", checked((ulong)secondSequence), 1));

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        await using var database = await OpenAsync(directory.Path, PantsRecoveryPolicy.Strict);

        Assert.Equal(expectedBoundary, report.WalBoundary);
        Assert.Equal(2, report.WalRecoveryRecordsReplayed);
        Assert.Equal("one", await ReadAsync(database, "first"));
        Assert.Equal("two", await ReadAsync(database, "second"));
        Assert.Equal(expectedBoundary, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
    }

    static async Task InitializeDatabaseAsync(string path)
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(path));
    }

    static async Task<uint> InitializeWithDroppedFamilyAsync(string path)
    {
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path).WithBackgroundCompaction(false));
        var family = await database.ColumnFamilies.CreateAsync("dropped");
        await database.ColumnFamilies.DropAsync(family);
        return family.Id;
    }

    static async Task SeedPersistedFrontierAsync(string path)
    {
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path).WithBackgroundCompaction(false));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 8; index++)
        {
            transaction.Put(
                TestBytes.FromString($"persisted-{index}"),
                TestBytes.FromString("value"));
        }

        await transaction.CommitAsync(PantsWriteOptions.Sync);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        PantsRecoveryPolicy recoveryPolicy) =>
        PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path)
                .WithRecoveryPolicy(recoveryPolicy)
                .WithBackgroundCompaction(false));

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    static byte[] CreatePut(
        string key,
        string value,
        ulong sequence,
        ulong writerEpoch,
        uint columnFamilyId = 0) =>
        WalCodec.EncodeRecord(new WalRecord(
            columnFamilyId,
            WalOperation.Put,
            TestBytes.FromString(key),
            TestBytes.FromString(value),
            sequence,
            null,
            null,
            null,
            writerEpoch));

    static WalMutation CreateMutation(
        string key,
        string value,
        ulong sequence,
        uint columnFamilyId = 0) => new(
        columnFamilyId,
        WalOperation.Put,
        TestBytes.FromString(key),
        TestBytes.FromString(value),
        sequence,
        null,
        null);

    static byte[] CreateDelete(string key, ulong sequence, ulong writerEpoch) =>
        WalCodec.EncodeRecord(new WalRecord(
            0,
            WalOperation.Delete,
            TestBytes.FromString(key),
            null,
            sequence,
            null,
            null,
            null,
            writerEpoch));

    static WalMutation CreateDeleteMutation(string key, ulong sequence) => new(
        0,
        WalOperation.Delete,
        TestBytes.FromString(key),
        null,
        sequence,
        null,
        null);

    static Task WriteWalAsync(string path, params byte[][] payloads) =>
        WriteWalFileAsync(path, "wal.log", payloads);

    static Task WriteWalFileAsync(string path, string fileName, params byte[][] payloads) =>
        File.WriteAllBytesAsync(Path.Combine(path, "wal", fileName), Frame(payloads));

    static async Task WriteWalWithCorruptTailAsync(string path, params byte[][] payloads)
    {
        var bytes = Frame(payloads);
        var corruptPayload = CreatePut("corrupt", "tail", 11, 2);
        using var stream = new MemoryStream(bytes.Length + corruptPayload.Length + 8);
        stream.Write(bytes);
        DiskFormat.WriteUInt32(stream, checked((uint)corruptPayload.Length));
        DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(corruptPayload) ^ uint.MaxValue);
        stream.Write(corruptPayload);
        await File.WriteAllBytesAsync(Path.Combine(path, "wal", "wal.log"), stream.ToArray());
    }

    static Task WriteWalWithCorruptedLengthBeforeValidSuffixAsync(string path)
    {
        var prefix = Frame([CreatePut("prefix", "preserved", 1, 1)]);
        var corruptLength = Frame(
            [CreatePut("corrupt-length", "discarded", 2, 1)]);
        var hiddenSuffix = Frame(
            [CreatePut("hidden-suffix", "discarded", 3, 1)]);
        var bytes = new byte[prefix.Length + corruptLength.Length + hiddenSuffix.Length];
        prefix.CopyTo(bytes, 0);
        corruptLength.CopyTo(bytes, prefix.Length);
        hiddenSuffix.CopyTo(bytes, prefix.Length + corruptLength.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(prefix.Length),
            checked((uint)bytes.Length));
        return File.WriteAllBytesAsync(Path.Combine(path, "wal", "wal.log"), bytes);
    }

    static Task WriteWalWithDuplicateLogicalVersionAsync(string path, string duplicateValue) =>
        File.WriteAllBytesAsync(
            Path.Combine(path, "wal", "wal.log"),
            CreateDuplicateLogicalVersionWal(duplicateValue));

    static byte[] CreateDuplicateLogicalVersionWal(
        string duplicateValue,
        uint columnFamilyId = 0) =>
        Frame(
        [
            CreatePut(
                "duplicate",
                "original",
                7,
                1,
                columnFamilyId),
            WalCodec.EncodeTransactionBatch(
                9,
                6,
                1,
                [CreateMutation("duplicate", duplicateValue, 0, columnFamilyId)])
        ]);

    static void DeleteSealedWalSegments(string path)
    {
        foreach (var walPath in Directory.EnumerateFiles(
                     Path.Combine(path, "wal"),
                     "*.wal",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(walPath);
        }
    }

    static async Task AssertDuplicateRecoveryAsync(
        string path,
        PantsRecoveryPolicy recoveryPolicy)
    {
        if (recoveryPolicy == PantsRecoveryPolicy.Strict)
        {
            var exception =
                await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => OpenAsync(path, recoveryPolicy).AsTask());
            Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
            return;
        }

        await using var database = await OpenAsync(path, recoveryPolicy);
        Assert.Equal(PantsEngineHealth.SalvageMode, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(path, "wal"),
            "*.salvage-retained*"));
    }

    static byte[] Frame(IEnumerable<byte[]> payloads)
    {
        using var stream = new MemoryStream();
        foreach (var payload in payloads)
        {
            DiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
            DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(payload));
            stream.Write(payload);
        }

        return stream.ToArray();
    }
}
