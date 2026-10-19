using System.Text.Json.Nodes;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class LegacyWalSegmentNamingRecoveryTests
{
    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public async Task ShouldRecoverSealedSegmentGivenLegacyWalFileName()
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            telemetry);
        using (var store = LocalDiskStore.Open(directory.Path, state))
        {
            _ = store.AppendCommit(
                CreateCommitPayload(state),
                state,
                PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();
        }

        var walDirectory = Path.Combine(directory.Path, "wal");
        var sealedSegments = Directory.EnumerateFiles(walDirectory, "*.wal").ToArray();
        var sealedSegment = Assert.Single(sealedSegments);
        var legacyPath = Path.Combine(walDirectory, "wal_000042.log");
        File.Move(sealedSegment, legacyPath);
        await MarkLegacyWalSequenceReservationVersionAsync(directory.Path);

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("legacy-key"u8.ToArray());
        Assert.Equal(
            "legacy-value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
    }

    static async Task MarkLegacyWalSequenceReservationVersionAsync(string path)
    {
        foreach (var fileName in new[] { "manifest.snapshot.json", "manifest.json" })
        {
            var manifestPath = Path.Combine(path, fileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            manifest["wal_sequence_reservation_version"] = 0;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        }
    }

    static CommitPayload CreateCommitPayload(RuntimeState state)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            "legacy-key"u8.ToArray(),
            null,
            "legacy-value"u8.ToArray(),
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        return new CommitPayload(
            1,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }
}
