using System.Buffers.Binary;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsWalWriterEpochRecoveryTests
{
    [Theory]
    [InlineData(PantsRecoveryPolicy.Strict)]
    [InlineData(PantsRecoveryPolicy.Salvage)]
    public async Task ShouldRejectSequenceRegressionOrSalvageItsVerifiedPrefix(PantsRecoveryPolicy recoveryPolicy)
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalAsync(directory.Path, CreatePut("legacy", "preserved", 1, 1), CreatePut("target", "fresh", 10, 1), CreatePut("target", "stale", 2, 1));
        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
        if (recoveryPolicy == PantsRecoveryPolicy.Strict)
        {
            await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path, recoveryPolicy).AsTask());
            return;
        }

        await using var recovered = await OpenAsync(directory.Path, recoveryPolicy);
        Assert.Equal("preserved", await ReadAsync(recovered, "legacy"));
        Assert.Equal("fresh", await ReadAsync(recovered, "target"));
        Assert.Equal(PantsEngineHealth.SalvageMode, (await recovered.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldRejectRegressingSequenceWithinOnePhysicalSegment()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalAsync(directory.Path, CreatePut("first", "one", 3, 1), CreatePut("second", "two", 2, 1));
        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());
    }

    [Fact]
    public async Task ShouldRecoverIncreasingEpochsAcrossLinkedSealedAndActiveSegments()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        WriteEpochSegments(directory.Path, (1, [CreatePut("first", "one", 1, 1)]), (2, [CreatePut("second", "two", 2, 2)]));
        await using var database = await OpenAsync(directory.Path, PantsRecoveryPolicy.Strict);
        Assert.Equal("one", await ReadAsync(database, "first"));
        Assert.Equal("two", await ReadAsync(database, "second"));
    }

    [Fact]
    public async Task ShouldFailStrictRecoveryGivenCorruptTransactionBatchBeforeAnotherRecord()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        var staleBatch = WalCodec.EncodeTransactionBatch(
            7,
            10,
            1,
            [CreateMutation("stale", "hidden", 0)]);
        var staleRecord = WalCodec.DecodeRecord(staleBatch);
        staleRecord.Value![0] = (byte)'X';
        staleBatch = WalCodec.EncodeRecord(staleRecord);
        await WriteWalAsync(
            directory.Path,
            staleBatch,
            CreatePut("fresh", "visible", 10, 2));

        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldFailStrictRecoveryGivenDuplicateTransactionBegin()
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
    public async Task ShouldIsolateReusedTransactionIdAcrossLinkedEpochSegments()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        WriteEpochSegments(directory.Path,
            (11, [WalCodec.EncodeTransactionMarker(WalOperation.TransactionBegin, 1, 1, 11), WalCodec.EncodeTransactionMutation(CreateMutation("orphaned", "hidden", 2), 1, 11)]),
            (12, [WalCodec.EncodeTransactionMarker(WalOperation.TransactionBegin, 1, 3, 12), WalCodec.EncodeTransactionMutation(CreateMutation("committed", "visible", 4), 1, 12), WalCodec.EncodeTransactionMarker(WalOperation.TransactionCommit, 1, 5, 12)]));
        await using var database = await OpenAsync(directory.Path, PantsRecoveryPolicy.Strict);
        Assert.Null(await ReadAsync(database, "orphaned"));
        Assert.Equal("visible", await ReadAsync(database, "committed"));
    }

    [Fact]
    public async Task ShouldSalvageVerifiedSequencePrefixGivenRegressingRecordAndCorruptTail()
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
    public async Task ShouldRejectRepeatedSequenceAcrossLinkedSegmentsEvenGivenIdenticalValue()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        WriteEpochSegments(directory.Path, (1, [CreatePut("duplicate", "original", 7, 1)]), (1, [CreatePut("duplicate", "original", 7, 1)]));
        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());
    }

    [Fact]
    public async Task ShouldRejectRepeatedSequenceAcrossLinkedSegmentsGivenConflictingValue()
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        WriteEpochSegments(directory.Path, (1, [CreatePut("duplicate", "original", 7, 1)]), (1, [CreatePut("duplicate", "conflicting", 7, 1)]));
        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());
    }

    [Fact]
    public async Task ShouldRejectDuplicateLogicalVersionGivenOnlineVerification()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await WriteRawFramesAsync(directory.Path, CreateDuplicateLogicalVersionWal("conflicting"));

        var exception =
            await Assert.ThrowsAsync<PantsCorruptionException>(() =>
                database.PersistentStorage!.VerifyAsync(TestTimeouts.Expected).AsTask());

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
        await WriteRawFramesAsync(directory.Path, CreateDuplicateLogicalVersionWal("conflicting", columnFamilyId));

        if (!droppedFamily)
        {
            // Unknown authority cannot authorize a salvage rewrite, even when another record
            // independently violates the WAL sequence contract.
            await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path, recoveryPolicy).AsTask());
            return;
        }

        await AssertDuplicateRecoveryAsync(directory.Path, recoveryPolicy);
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(7, 7)]
    public async Task ShouldRejectNonIncreasingRecordSequencesGivenDistinctKeys(int firstSequence, int secondSequence)
    {
        using var directory = new TemporaryDirectory();
        await InitializeDatabaseAsync(directory.Path);
        await WriteWalAsync(directory.Path, CreatePut("first", "one", (ulong)firstSequence, 1), CreatePut("second", "two", (ulong)secondSequence, 1));
        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());
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

    static void WriteEpochSegments(string path, params (ulong Epoch, byte[][] Payloads)[] segments)
    {
        var directory = Path.Combine(path, "wal");
        var holder = WalLedgerStore.LoadExisting(directory).Holder;
        foreach (var existing in Directory.GetFiles(directory, "*.wal"))
        {
            File.Delete(existing);
        }

        var previous = WalChainLink.Genesis;
        var members = new List<WalLedgerMember>();
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var id = (ulong)index + 1;
            var first = WalCodec.DecodeRecord(segment.Payloads[0]).Sequence;
            var open = new WalOpenFacts(id, segment.Epoch, first, previous);
            var walPath = Path.Combine(directory, $"{id:00000000000000000000}.wal");
            using var stream = new FileStream(walPath, FileMode.Create, FileAccess.ReadWrite);
            stream.Write(WalSegmentCodec.EncodeHeader(open, holder));
            stream.Write(Frame(segment.Payloads));
            if (index < segments.Length - 1)
            {
                var facts = WalSegmentCodec.Seal(stream);
                members.Add(new WalLedgerMember(open, facts, facts.DataLen));
                previous = facts.Link;
            }
            else
            {
                members.Add(new WalLedgerMember(open, null, (ulong)stream.Length));
            }
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["Snapshot"] = new { holder, anchor = WalChainLink.Genesis, members, next_segment_id = (ulong)segments.Length + 1, seq_reserved_through = 100UL }
        }, WalLedgerStore.JsonOptions);
        var bytes = new byte[20 + payload.Length];
        "MDGLEDGR"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(bytes, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), DiskFormat.Crc32CAppend(DiskFormat.Crc32C(bytes.AsSpan(12, 4)), payload));
        File.WriteAllBytes(Path.Combine(directory, "LEDGER"), bytes);
    }

    static Task WriteWalAsync(string path, params byte[][] payloads)
    {
        WalTestFiles.ReplaceActiveRecords(path, payloads);
        return Task.CompletedTask;
    }

    static Task WriteRawFramesAsync(string path, byte[] frames)
    {
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(path, "wal"));
        var header = WalSegmentCodec.EncodeHeader(ledger.Active.Open, ledger.Holder);
        var bytes = header.Concat(frames).ToArray();
        File.WriteAllBytes(ledger.ActivePath, bytes);
        ledger.SyncOpen(checked((ulong)bytes.Length));
        ledger.Reserve(Math.Max(ledger.ReservedThrough, 100));
        return Task.CompletedTask;
    }

    static async Task WriteWalWithCorruptTailAsync(string path, params byte[][] payloads)
    {
        var bytes = Frame(payloads);
        var corruptPayload = CreatePut("corrupt", "tail", 11, 2);
        using var stream = new MemoryStream(bytes.Length + corruptPayload.Length + 8);
        stream.Write(bytes);
        DiskFormat.WriteUInt32(stream, checked((uint)corruptPayload.Length));
        DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(corruptPayload) ^ uint.MaxValue);
        stream.Write(corruptPayload);
        await WriteRawFramesAsync(path, stream.ToArray());
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
        return WriteRawFramesAsync(path, bytes);
    }

    static Task WriteWalWithDuplicateLogicalVersionAsync(string path, string duplicateValue) =>
        WriteRawFramesAsync(path, CreateDuplicateLogicalVersionWal(duplicateValue));

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
        var directory = Path.Combine(path, "wal");
        var prior = WalLedgerStore.LoadExisting(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.wal"))
        {
            File.Delete(file);
        }

        var open = new WalOpenFacts(1, 1, 1, WalChainLink.Genesis);
        File.WriteAllBytes(Path.Combine(directory, "00000000000000000001.wal"), WalSegmentCodec.EncodeHeader(open, prior.Holder));
        new WalLedgerStore(directory, prior.Holder, WalChainLink.Genesis, [new WalLedgerMember(open, null, 128)], 2, prior.ReservedThrough).Save();
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
