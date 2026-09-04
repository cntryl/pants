using System.Text;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeCompatibilityFixtureTests
{
    [Fact]
    public async Task ShouldVerifyPopulatedReleaseV3V4FixtureGivenSupportedFormatWhenReopening()
    {
        using var directory = MidgeCompatibilityFixture.CopyToTemporaryDirectory(
            "v3_populated_v4_sst_db");

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(PantsEngineHealth.Healthy, report.Health);
        Assert.Equal(1, report.ManifestEpoch);
        Assert.Equal(1, report.ManifestFilesVerified);
        Assert.Equal(1, report.SstFilesVerified);
        Assert.Equal(437, report.BytesVerified);
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
        var structuredValue = string.Concat(Enumerable.Repeat(structuredPattern, 432))[..16_384];
        Assert.Equal(("fixture/structured", structuredValue), rows[2]);
    }

    [Fact]
    public async Task ShouldRejectV2EmptyFixtureGivenBreakingV4SstFormat()
    {
        using var directory = MidgeCompatibilityFixture.CopyToTemporaryDirectory("v2_empty_db");

        var verifyError =
            await Assert.ThrowsAsync<PantsCompatibilityException>(() =>
                PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
        var openError = await Assert.ThrowsAsync<PantsCompatibilityException>(() =>
            PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());

        Assert.Equal(PantsErrorCode.CompatibilityError, verifyError.Code);
        Assert.Equal(PantsErrorCode.CompatibilityError, openError.Code);
    }

    [Fact]
    public async Task ShouldRejectFutureV4FixtureGivenUnsupportedVersionWhenReopening()
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
