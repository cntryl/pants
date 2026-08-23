using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Pants.Tests;

public sealed class PantsSstIndexTuningTests
{
    [Fact]
    public void ShouldKeepMidgeIndexKindDiscriminantsStable()
    {
        Assert.Equal(0, (byte)MidgeSstIndexKind.Sparse);
        Assert.Equal(1, (byte)MidgeSstIndexKind.Trie);
    }

    [Fact]
    public void ShouldChooseTrieGivenStructuredKeyProfile()
    {
        var profile = new KeyStructureProfile(
            AverageSharedPrefix: 8,
            MaximumSharedPrefix: 12,
            PrefixDivergence: 192,
            Entropy: 3,
            CommonPrefixLength: 2,
            KeyLengthStandardDeviation: 0,
            PrefixHeat: [],
            KeyCount: 192);

        Assert.Equal(MidgeSstIndexKind.Trie, MidgeSstIndexTuner.Decide(profile));
    }

    [Fact]
    public void ShouldProfileStructuredAndHashLikeKeysDifferently()
    {
        byte[][] structuredKeys = Enumerable.Range(0, 192)
            .Select(index => Encoding.UTF8.GetBytes($"tenant/shared/static-segment/{index:0000}"))
            .ToArray();
        byte[][] hashLikeKeys = Enumerable.Range(0, 256)
            .Select(static index => SHA256.HashData(BitConverter.GetBytes(index)))
            .OrderBy(static key => key, ByteArrayComparer.Instance)
            .ToArray();
        var structuredProfiler = new KeyStructureProfiler();
        var hashLikeProfiler = new KeyStructureProfiler();
        foreach (byte[] key in structuredKeys)
        {
            structuredProfiler.Add(key);
        }

        foreach (byte[] key in hashLikeKeys)
        {
            hashLikeProfiler.Add(key);
        }

        KeyStructureProfile structured = structuredProfiler.Finish();
        KeyStructureProfile hashLike = hashLikeProfiler.Finish();

        Assert.True(structured.AverageSharedPrefix > 20);
        Assert.True(structured.CommonPrefixLength > 20);
        Assert.Equal(1, structured.PrefixDivergence);
        Assert.True(hashLike.Entropy > 7.9F);
        Assert.True(hashLike.AverageSharedPrefix < 1.0F);
        Assert.Equal(0, hashLike.CommonPrefixLength);
        Assert.Equal(MidgeSstIndexKind.Trie, MidgeSstIndexTuner.Decide(structured));
        Assert.Equal(MidgeSstIndexKind.Sparse, MidgeSstIndexTuner.Decide(hashLike));
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
            AverageSharedPrefix: 8,
            MaximumSharedPrefix: 12,
            PrefixDivergence: prefixDivergence,
            Entropy: (float)entropy,
            CommonPrefixLength: 2,
            KeyLengthStandardDeviation: 0,
            PrefixHeat: [],
            KeyCount: keyCount);

        Assert.Equal(MidgeSstIndexKind.Sparse, MidgeSstIndexTuner.Decide(profile));
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

        byte[] encoded = MidgeTrieIndex.Encode(keys);
        MidgeTrieIndex index = MidgeTrieIndex.Decode(encoded, keys);

        Assert.Equal(-1, index.FindFloorBlock("a"u8));
        Assert.Equal(0, index.FindFloorBlock("tenant/shared/0050"u8));
        Assert.Equal(1, index.FindFloorBlock("tenant/shared/0199"u8));
        Assert.Equal(2, index.FindFloorBlock("tenant/shared/9999"u8));
    }

    [Fact]
    public void ShouldPersistTrieIndexGivenStructuredKeys()
    {
        MidgeSstEntry[] entries = Enumerable.Range(0, 192)
            .Select(index => new MidgeSstEntry(
                Encoding.UTF8.GetBytes($"tenant/shared/static-segment/{index:0000}"),
                new byte[1024],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();

        byte[] bytes = MidgeSstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        MidgeSstContents contents = MidgeSstCodec.Decode(bytes);

        Assert.Equal(MidgeSstIndexKind.Trie, MidgeSstCodec.GetIndexKind(bytes));
        Assert.Equal(entries.Select(static entry => entry.Key), contents.Entries.Select(static entry => entry.Key));
        Assert.True(MidgeSstCodec.GetPointReadDecision(bytes, entries[100].Key).CandidateBlockIndex >= 0);
    }

    [Fact]
    public void ShouldPersistSparseIndexGivenSmallSst()
    {
        MidgeSstEntry[] entries = Enumerable.Range(0, 64)
            .Select(index => new MidgeSstEntry(
                Encoding.UTF8.GetBytes($"random-key-{index:0000}"),
                "value"u8.ToArray(),
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();

        byte[] bytes = MidgeSstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        MidgeSstContents contents = MidgeSstCodec.Decode(bytes);

        Assert.Equal(MidgeSstIndexKind.Sparse, MidgeSstCodec.GetIndexKind(bytes));
        Assert.Equal(entries.Select(static entry => entry.Key), contents.Entries.Select(static entry => entry.Key));
    }

    [Fact]
    public void ShouldReadTrieGivenRepeatedFirstKeysAcrossDataBlocks()
    {
        MidgeSstEntry[] entries = Enumerable.Range(0, 192)
            .Select(index => new MidgeSstEntry(
                "aaaa"u8.ToArray(),
                new byte[1024],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();

        byte[] bytes = MidgeSstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        MidgeSstContents contents = MidgeSstCodec.Decode(bytes);

        Assert.Equal(MidgeSstIndexKind.Trie, MidgeSstCodec.GetIndexKind(bytes));
        Assert.Equal(entries.Length, contents.Entries.Count);
    }
}
