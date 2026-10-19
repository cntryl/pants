using System.Security.Cryptography;
using System.Text;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstIndexTuningTests
{
    [Fact]
    public void ShouldKeepMidgeIndexKindDiscriminantsStable()
    {
        Assert.Equal(0, (byte)SstIndexKind.Sparse);
        Assert.Equal(1, (byte)SstIndexKind.Trie);
    }

    [Fact]
    public void ShouldChooseTrieGivenStructuredKeyProfile()
    {
        var profile = new KeyStructureProfile(
            8,
            12,
            192,
            3,
            2,
            0,
            [],
            192);

        Assert.Equal(SstIndexKind.Trie, SstIndexTuner.Decide(profile));
    }

    [Fact]
    public void ShouldProfileStructuredAndHashLikeKeysDifferently()
    {
        var structuredKeys = Enumerable.Range(0, 192)
            .Select(index => Encoding.UTF8.GetBytes($"tenant/shared/static-segment/{index:0000}"))
            .ToArray();
        var hashLikeKeys = Enumerable.Range(0, 256)
            .Select(static index => SHA256.HashData(BitConverter.GetBytes(index)))
            .OrderBy(static key => key, ByteArrayComparer.Instance)
            .ToArray();
        var structuredProfiler = new KeyStructureProfiler();
        var hashLikeProfiler = new KeyStructureProfiler();
        foreach (var key in structuredKeys)
        {
            structuredProfiler.Add(key);
        }

        foreach (var key in hashLikeKeys)
        {
            hashLikeProfiler.Add(key);
        }

        var structured = structuredProfiler.Finish();
        var hashLike = hashLikeProfiler.Finish();

        Assert.True(structured.AverageSharedPrefix > 20);
        Assert.True(structured.CommonPrefixLength > 20);
        Assert.Equal(1, structured.PrefixDivergence);
        Assert.True(hashLike.Entropy > 7.9F);
        Assert.True(hashLike.AverageSharedPrefix < 1.0F);
        Assert.Equal(0, hashLike.CommonPrefixLength);
        Assert.Equal(SstIndexKind.Trie, SstIndexTuner.Decide(structured));
        Assert.Equal(SstIndexKind.Sparse, SstIndexTuner.Decide(hashLike));
    }

    [Theory]
    [InlineData(127, 3.0, 32)]
    [InlineData(128, 4.1, 32)]
    [InlineData(128, 3.0, 1024)]
    public void ShouldChooseSparseGivenMidgeGuardrail(
        int keyCount,
        double entropy,
        int prefixDivergence)
    {
        var profile = new KeyStructureProfile(
            8,
            12,
            prefixDivergence,
            (float)entropy,
            2,
            0,
            [],
            keyCount);

        Assert.Equal(SstIndexKind.Sparse, SstIndexTuner.Decide(profile));
    }

    [Fact]
    public void ShouldRoundTripMidgeTrieAndFindFloorBlock()
    {
        byte[][] keys =
        [
            "tenant/shared/0000"u8.ToArray(),
            "tenant/shared/0100"u8.ToArray(),
            "tenant/shared/0200"u8.ToArray()
        ];

        var encoded = TrieIndex.Encode(keys);
        var index = TrieIndex.Decode(encoded, keys);

        Assert.Equal(-1, index.FindFloorBlock("a"u8));
        Assert.Equal(0, index.FindFloorBlock("tenant/shared/0050"u8));
        Assert.Equal(1, index.FindFloorBlock("tenant/shared/0199"u8));
        Assert.Equal(2, index.FindFloorBlock("tenant/shared/9999"u8));
    }

    [Fact]
    public void ShouldPersistTrieIndexGivenStructuredKeys()
    {
        var entries = Enumerable.Range(0, 192)
            .Select(index => new SstEntry(
                Encoding.UTF8.GetBytes($"tenant/shared/static-segment/{index:0000}"),
                new byte[1024],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();

        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        var contents = SstCodec.Decode(bytes);

        Assert.Equal(SstIndexKind.Trie, SstCodec.GetIndexKind(bytes));
        Assert.Equal(entries.Select(static entry => entry.Key), contents.Entries.Select(static entry => entry.Key));
        Assert.True(SstCodec.GetPointReadDecision(bytes, entries[100].Key).CandidateBlockIndex >= 0);
    }

    [Fact]
    public void ShouldPersistSparseIndexGivenSmallSst()
    {
        var entries = Enumerable.Range(0, 64)
            .Select(index => new SstEntry(
                Encoding.UTF8.GetBytes($"random-key-{index:0000}"),
                "value"u8.ToArray(),
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();

        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        var contents = SstCodec.Decode(bytes);

        Assert.Equal(SstIndexKind.Sparse, SstCodec.GetIndexKind(bytes));
        Assert.Equal(entries.Select(static entry => entry.Key), contents.Entries.Select(static entry => entry.Key));
    }

    [Fact]
    public void ShouldReadTrieGivenRepeatedFirstKeysAcrossDataBlocks()
    {
        var entries = Enumerable.Range(0, 192)
            .Select(index => new SstEntry(
                "aaaa"u8.ToArray(),
                new byte[1024],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();

        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        var contents = SstCodec.Decode(bytes);

        Assert.Equal(SstIndexKind.Trie, SstCodec.GetIndexKind(bytes));
        Assert.Equal(entries.Length, contents.Entries.Count);
    }

    [Fact]
    public async Task ShouldReadEmptyKeyAndThreeHundredLevelSharedPrefixAfterFlushVerificationAndReopen()
    {
        using var directory = new TemporaryDirectory();
        var prefix = Enumerable.Repeat((byte)'p', 300).ToArray();
        var keys = Enumerable.Range(0, 300)
            .Select(index => prefix.Concat(Encoding.UTF8.GetBytes($"{index:D4}")).ToArray())
            .ToArray();
        var values = Enumerable.Range(0, keys.Length)
            .Select(index => TestBytes.FromString($"value-{index}"))
            .ToArray();

        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
            await using var writer = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            writer.Put(ReadOnlyMemory<byte>.Empty, TestBytes.FromString("empty-key"));
            for (var index = 0; index < keys.Length; index++)
            {
                writer.Put(keys[index], values[index]);
            }

            await writer.CommitAsync(PantsWriteOptions.Sync);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

            var sstPath = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(directory.Path, "sst"),
                "*.sst"));
            Assert.Equal(SstIndexKind.Trie, SstCodec.GetIndexKind(File.ReadAllBytes(sstPath)));

            var report = await database.PersistentStorage!.VerifyAsync(TestTimeouts.Expected);
            Assert.True(report.Authoritative);
            Assert.Equal(1, report.SstFilesVerified);

            await using var reader = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            Assert.Equal(
                "empty-key",
                TestBytes.ToText((await reader.GetAsync(ReadOnlyMemory<byte>.Empty))!.Value));
            for (var index = 0; index < keys.Length; index++)
            {
                Assert.Equal(values[index], (await reader.GetAsync(keys[index]))!.Value.ToArray());
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reopenedReader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "empty-key",
            TestBytes.ToText((await reopenedReader.GetAsync(ReadOnlyMemory<byte>.Empty))!.Value));
        for (var index = 0; index < keys.Length; index++)
        {
            Assert.Equal(values[index], (await reopenedReader.GetAsync(keys[index]))!.Value.ToArray());
        }
    }
}
