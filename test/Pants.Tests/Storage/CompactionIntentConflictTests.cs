using System.Text.Json;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.Recovery;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

/// <summary>
///     A manifest-published compaction intent that recovery could not settle stays in the
///     persisted intent log. A later compaction attempt, however it is keyed, must be rejected
///     without touching that intent or the files it names.
/// </summary>
public sealed class CompactionIntentConflictTests
{
    const string OutputName = "000000_01_00000000000000009999.sst";

    static readonly JsonSerializerOptions NullPreservingOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectCompactionAndLeaveIntentUnmodifiedGivenManifestPublishedIntentKeyedByInputsOrOutputs(
        bool sameInputs)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        await SeedFlushedFilesAsync(directory.Path);
        var inputs = SstNames(directory.Path);
        Assert.True(inputs.Length >= 2);
        var intentLogPath = Path.Combine(directory.Path, "intent_log.json");
        var intent = IntentEntry.Create("CompactionPublish", new
        {
            phase = "ManifestPublished",
            cf_id = 0U,
            removed = sameInputs ? inputs : inputs.Take(1).ToArray(),
            added = new[]
            {
                new
                {
                    name = OutputName,
                    level = 1U,
                    size_bytes = 1UL,
                    cf_id = 0U,
                    smallest_key = (byte[]?)null,
                    largest_key = (byte[]?)null,
                    smallest_seq = (ulong?)null,
                    largest_seq = (ulong?)null
                }
            }
        }, NullPreservingOptions);
        await File.WriteAllBytesAsync(
            intentLogPath,
            IntentLogCodec.CreateEdit(IntentLogCodec.Read([]), [intent]));
        var intentLogBefore = await File.ReadAllBytesAsync(intentLogPath);
        var sstBefore = inputs.ToDictionary(
            name => name,
            name => File.ReadAllBytes(Path.Combine(directory.Path, "sst", name)));
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(
            directory.Path,
            state,
            recoveryPolicy: PantsRecoveryPolicy.Salvage);

        // Act
        var exception = await Assert.ThrowsAsync<PantsBusyException>(() =>
            store.CompactAsync(state, true, null, false).AsTask());

        // Assert
        Assert.Contains("fenced", exception.Message, StringComparison.Ordinal);
        Assert.Equal(intentLogBefore, await File.ReadAllBytesAsync(intentLogPath));
        Assert.Equal(inputs, SstNames(directory.Path));
        foreach (var (name, bytes) in sstBefore)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "sst", name)));
        }
    }

    static async Task SeedFlushedFilesAsync(string root)
    {
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(root).WithBackgroundCompaction(false));
        var family = database.ColumnFamilies.DefaultFamily;
        for (var flush = 0; flush < 2; flush++)
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put(TestBytes.FromString($"conflict-{flush}"), new byte[256]);
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(family);
        }
    }

    static string[] SstNames(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "sst"), "*.sst", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
}
