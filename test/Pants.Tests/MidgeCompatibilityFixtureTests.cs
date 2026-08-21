using System.Text;

namespace Pants.Tests;

public sealed class MidgeCompatibilityFixtureTests
{
    private const string ReleaseSstV4Base64 =
        "yQAAAHVAAACsAAANAAsAAAACAAEA8QRmaXh0dXJlL2FscGhhdmFsdWUtCwAxCAAFKwARAwYACAIA6GVtcHR5CAAKAABAAAAEGgABAgBQc3RydWNTAP8YZGFjY291bnQ9MDA0MnxyZWdpb249ZWFzdHxzdGF0ZT1zdGFibGV8JgD/////////////////////////////////////////////////////////////////////////////////////AWBhY2NvdW4BgzwQJx4AAAABAAAAAAAAAEAAAAADAAAACACeCRVQYiXEAK31Q4JEAAAABAAAAAABAAAAAAAAAAAAAAAAAAAAAAAADQAAAGZpeHR1cmUvYWxwaGESAAAAZml4dHVyZS9zdHJ1Y3R1cmVkADOAEcomAAAADQAAAGZpeHR1cmUvYWxwaGEAAAAAAAAAAM0AAAAAAAAAAIL76MjvAAAAAAAAAEgAAAAAAAAANwEAAAAAAAAqAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADNAAAAAAAAACIAAAAAAAAABAAAAFQAAABX+4CLJHVH221jlTU=";

    private const string ReleaseManifest = """
        {
          "last_persisted_sequence": 5,
          "files": [
            {
              "name": "000000_00_00000000000000000001.sst",
              "level": 0,
              "size_bytes": 437,
              "content_crc32c": 1313763752,
              "cf_id": 0,
              "sst_seq": 0,
              "smallest_key": [102, 105, 120, 116, 117, 114, 101, 47, 97, 108, 112, 104, 97],
              "largest_key": [102, 105, 120, 116, 117, 114, 101, 47, 115, 116, 114, 117, 99, 116, 117, 114, 101, 100],
              "smallest_seq": 2,
              "largest_seq": 4,
              "sublevel": 0
            }
          ],
          "column_families": [],
          "next_wal_seq": 1,
          "next_sst_seqs": { "0": 2 },
          "edit_checkpoint_id": 1
        }
        """;

    [Fact]
    public async Task ShouldOpenPinnedMidgeV3V4ReleaseFixture()
    {
        using var directory = new TemporaryDirectory();
        await WriteReleaseFixtureAsync(directory.Path);

        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using IPantsScan scan = await transaction.ScanAsync(new PantsScanQuery());
        List<(string Key, string Value)> rows = [];
        await foreach (PantsEntry entry in scan)
        {
            rows.Add((Encoding.UTF8.GetString(entry.Key.Span), Encoding.UTF8.GetString(entry.Value.Span)));
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal("fixture/alpha", rows[0].Key);
        Assert.Equal("value-alpha", rows[0].Value);
        Assert.Equal("fixture/empty", rows[1].Key);
        Assert.Equal(string.Empty, rows[1].Value);
        Assert.Equal("fixture/structured", rows[2].Key);
        const string structuredPattern = "account=0042|region=east|state=stable|";
        string structuredValue = string.Concat(Enumerable.Repeat(structuredPattern, 432))[..16_384];
        Assert.Equal(structuredValue, rows[2].Value);
    }

    [Fact]
    public async Task ShouldMatchPinnedMidgeOfflineVerificationReport()
    {
        using var directory = new TemporaryDirectory();
        await WriteReleaseFixtureAsync(directory.Path);

        PantsStorageVerificationReport report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(PantsEngineHealth.Healthy, report.Health);
        Assert.Equal(1, report.ManifestEpoch);
        Assert.Equal(1, report.ManifestFilesVerified);
        Assert.Equal(1, report.SstFilesVerified);
        Assert.Equal(437, report.BytesVerified);
        Assert.Equal(1, report.DataBlocksVerified);
        Assert.Null(report.WalBoundary);
        Assert.Equal(0, report.IntentEntriesLoaded);
        Assert.True(report.Authoritative);
    }

    private static async Task WriteReleaseFixtureAsync(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "sst"));
        Directory.CreateDirectory(Path.Combine(path, "wal"));
        await File.WriteAllTextAsync(Path.Combine(path, "FORMAT"), "midge-format-version=3\n");
        await File.WriteAllTextAsync(Path.Combine(path, "manifest.json"), ReleaseManifest);
        await File.WriteAllTextAsync(Path.Combine(path, "manifest.snapshot.json"), ReleaseManifest);
        await File.WriteAllTextAsync(Path.Combine(path, "intent_log.json"), "[]");
        await File.WriteAllBytesAsync(Path.Combine(path, "manifest.journal"), []);
        await File.WriteAllBytesAsync(Path.Combine(path, "wal", "wal.log"), []);
        await File.WriteAllBytesAsync(
            Path.Combine(path, "sst", "000000_00_00000000000000000001.sst"),
            Convert.FromBase64String(ReleaseSstV4Base64));
    }
}
