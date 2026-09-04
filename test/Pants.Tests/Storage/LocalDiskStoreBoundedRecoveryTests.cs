namespace Cntryl.Pants.Tests.Storage;

/// <summary>
/// Slice 4a (issue #219): normal recovery loads/validates manifest-SST metadata via bounded
/// positional reads (<see cref="SstReader.Open"/>) and replays only WAL mutations above the
/// manifest's durable per-family frontier — it must not decode every published SST's data
/// blocks. Strict-mode footer/metadata/index/checksum validation must still work without that
/// whole-corpus hydration; a corrupted *data block* is an explicitly deferred detection,
/// surfaced by the first read that touches it rather than by recovery itself.
/// </summary>
public sealed class LocalDiskStoreBoundedRecoveryTests
{
    [Fact]
    public async Task ShouldOpenSuccessfullyDespiteACorruptedDataBlockUnderNormalRecovery()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            // Large enough values that "key-0000" and "key-0127" land in different data blocks
            // (64 KiB target block size), so corrupting the first block leaves the last key's
            // block untouched.
            for (var index = 0; index < 128; index++)
            {
                await PutAsync(database, $"key-{index:D4}", new string('v', 4096) + index);
            }

            await database.FlushAsync(database.DefaultColumnFamily);
        }

        CorruptFirstSstDataByte(directory.Path);

        // Normal (Strict-policy) recovery must not decode data blocks, so a corrupted data byte
        // — as opposed to a corrupted footer/metadata/index/whole-file-checksum mismatch — does
        // not fail Open.
        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        // A key in an untouched block is unaffected — recovery, and the point read resolving
        // it, both worked despite the corruption elsewhere in the file.
        var value = await reader.GetAsync(TestBytes.FromString("key-0127"));
        Assert.Equal(new string('v', 4096) + 127, TestBytes.ToText(value!.Value));

        // The corruption in the first block is deferred, not silently ignored: it surfaces as a
        // corruption error at the first read that actually touches that block, per the issue's
        // explicitly allowed "detectable by ... the first affected read" boundary.
        await Assert.ThrowsAsync<StorageException>(
            () => reader.GetAsync(TestBytes.FromString("key-0000")).AsTask());
    }

    [Fact]
    public async Task ShouldStillFailOpenGivenAFooterCorruptionUnderNormalRecovery()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await PutAsync(database, "key", "value");
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        CorruptSstFooter(directory.Path);

        // Structural (footer/metadata/index) corruption is still detected without decoding any
        // data block — this is the bounded footer+meta+index+bloom read SstReader.Open already
        // performs.
        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());
    }

    [Fact]
    public async Task ShouldReconstructCorrectStateFromSstsAndWalTogetherAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await PutAsync(database, "flushed", "from-sst");
            await database.FlushAsync(database.DefaultColumnFamily);
            await PutAsync(database, "unflushed", "from-wal");
        }

        await using var reopened = await OpenAsync(directory.Path);

        Assert.Equal("from-sst", await ReadAsync(reopened, "flushed"));
        Assert.Equal("from-wal", await ReadAsync(reopened, "unflushed"));
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path).WithBackgroundCompaction(false));

    static async Task PutAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is { } present ? TestBytes.ToText(present) : null;
    }

    static void CorruptFirstSstDataByte(string databasePath)
    {
        var path = FirstSstPath(databasePath);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = sizeof(uint);
        var value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = sizeof(uint);
        stream.WriteByte(checked((byte)(value ^ 1)));
        stream.Flush(true);
    }

    static void CorruptSstFooter(string databasePath)
    {
        var path = FirstSstPath(databasePath);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = stream.Length - 1;
        var value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = stream.Length - 1;
        stream.WriteByte(checked((byte)(value ^ 1)));
        stream.Flush(true);
    }

    static string FirstSstPath(string databasePath) =>
        Directory.EnumerateFiles(Path.Combine(databasePath, "sst"), "*.sst", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .First();
}
