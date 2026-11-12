using System.Text;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeCompatibilityFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReadMidgeSstGivenMissingManifestBoundsWhenReopening(bool markedComplete)
    {
        // Arrange
        using var directory = Format9TestMetadata.CopyFixture("populated-local");
        foreach (var name in new[] { "manifest.snapshot.json" })
        {
            var path = Path.Combine(directory.Path, name);
            var root = Format9TestMetadata.ReadNode(path).AsObject();
            var file = root["files"]!.AsArray()[0]!.AsObject();
            file["level"] = 1;
            file["key_bounds_complete"] = markedComplete;
            file.Remove("smallest_key");
            file.Remove("largest_key");
            await Format9TestMetadata.WriteAsync(path, root.ToJsonString());
        }

        // Act
        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadOnly))
            {
                var point = await transaction.GetAsync("fixture/alpha"u8.ToArray());
                await using var scan = await transaction.ScanAsync(new PantsScanQuery
                {
                    StartInclusive = "fixture/alpha"u8.ToArray(),
                    EndExclusive = "fixture/empty"u8.ToArray()
                });
                var rows = new List<string>();
                await foreach (var entry in scan)
                {
                    rows.Add(Encoding.UTF8.GetString(entry.Key.Span));
                }

                Assert.Equal("value-alpha", Encoding.UTF8.GetString(point!.Value.Span));
                Assert.Equal(["fixture/alpha"], rows);
            }

            await database.ColumnFamilies.CreateAsync("checkpoint-marker");
        }

        await using (var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
            await using var reader = await reopened.Transactions.BeginAsync(
                reopened.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            var point = await reader.GetAsync("fixture/alpha"u8.ToArray());
            Assert.Equal("value-alpha", Encoding.UTF8.GetString(point!.Value.Span));
        }

    }

    [Fact]
    public async Task ShouldVerifyPopulatedFormat9V5FixtureWhenReopening()
    {
        using var directory = Format9TestMetadata.CopyFixture("populated-local");

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(PantsEngineHealth.Healthy, report.Health);
        Assert.Equal(1UL, report.ManifestEpoch);
        Assert.Equal(1, report.ManifestFilesVerified);
        Assert.Equal(1, report.SstFilesVerified);
        Assert.Equal(376, report.BytesVerified);
        Assert.Equal(1, report.DataBlocksVerified);
        Assert.Null(report.WalBoundary);
        Assert.Equal(0, report.WalRecoveryRecordsReplayed);
        Assert.Equal(0, report.WalRecoveryBytesReplayed);
        Assert.Equal(0, report.IntentEntriesLoaded);
        Assert.True(report.Authoritative);

        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal(PantsEngineHealth.Healthy, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);

        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var rows = new List<(string Key, string Value)>();
        await foreach (var entry in scan)
        {
            rows.Add((Encoding.UTF8.GetString(entry.Key.Span), Encoding.UTF8.GetString(entry.Value.Span)));
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(("fixture/alpha", "value-alpha"), rows[0]);
        Assert.Equal(("fixture/empty", string.Empty), rows[1]);
        const string structuredPattern = "account=0042|region=east|state=stable|";
        var structuredValue = string.Concat(Enumerable.Repeat(structuredPattern, 128));
        Assert.Equal(("fixture/structured", structuredValue), rows[2]);
    }

    [Theory]
    [InlineData("v2_empty_db")]
    [InlineData("v3_populated_v4_sst_db")]
    public async Task ShouldRejectLegacyFixtureGivenBreakingFormat9Profile(string fixture)
    {
        using var directory = MidgeCompatibilityFixture.CopyToTemporaryDirectory(fixture);

        var verifyError =
            await Assert.ThrowsAsync<PantsCompatibilityException>(() =>
                PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
        var openError = await Assert.ThrowsAsync<PantsCompatibilityException>(() =>
            PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());

        Assert.Equal(PantsErrorCode.CompatibilityError, verifyError.Code);
        Assert.Equal(PantsErrorCode.CompatibilityError, openError.Code);
    }

    [Fact]
    public async Task ShouldRejectLegacyV4FixtureGivenUnsupportedVersionWhenReopening()
    {
        using var directory = MidgeCompatibilityFixture.CopyToTemporaryDirectory("future_v4");

        var verifyError =
            await Assert.ThrowsAsync<PantsCompatibilityException>(() =>
                PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
        var openError = await Assert.ThrowsAsync<PantsCompatibilityException>(() =>
            PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());

        Assert.Equal(PantsErrorCode.CompatibilityError, verifyError.Code);
        Assert.Equal(PantsErrorCode.CompatibilityError, openError.Code);
    }
}
