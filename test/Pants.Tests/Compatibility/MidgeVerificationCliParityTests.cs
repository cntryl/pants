using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeVerificationCliParityTests
{
    const string VerificationFixturePath = "Fixtures/Compatibility/Midge/445a901d8c34ec6a1fca91038da6bb187caaa7b6/verification";

    [Fact]
    public async Task ShouldEmitV1CorruptionErrorJsonGivenFutureFormatWhenPantsVerifyRuns()
    {
        using var directory = MidgeCompatibilityFixture.CopyToTemporaryDirectory("future_v4");
        var output = await RunVerifyAsync("verify", "--json", directory.Path);
        var expected = LoadErrorGolden(
            "corruption_error_v1.json",
            "The path does not contain a valid Midge FORMAT v3 marker.");

        Assert.Equal(4, output.ExitCode);
        AssertJsonEqual(expected, output.StandardOutput);
        Assert.Empty(output.StandardError);
    }

    [Fact]
    public async Task ShouldEmitV1JsonGivenDegradedLocalDatabaseWhenPantsVerifyRuns()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
            await database.ShutdownAsync(TimeSpan.FromSeconds(30));
        }

        var sstPath = Path.Combine(directory.Path, "sst");
        Directory.CreateDirectory(sstPath);
        await File.WriteAllBytesAsync(Path.Combine(sstPath, "orphan.sst"), "orphan bytes"u8.ToArray());

        var output = await RunVerifyAsync("verify", "--json", directory.Path);

        Assert.Equal(1, output.ExitCode);
        AssertJsonEqual(LoadGolden("degraded_v1.json"), output.StandardOutput);
        Assert.Empty(output.StandardError);
    }

    [Fact]
    public async Task ShouldEmitV1JsonGivenPopulatedSupportedFixtureWhenPantsVerifyRuns()
    {
        using var directory = MidgeCompatibilityFixture.CopyToTemporaryDirectory(
            "v3_populated_v4_sst_db");

        var output = await RunVerifyAsync("verify", "--json", directory.Path);

        Assert.Equal(0, output.ExitCode);
        AssertJsonEqual(LoadGolden("healthy_v1.json"), output.StandardOutput);
        Assert.Empty(output.StandardError);
    }

    [Fact]
    public async Task ShouldEmitV1StorageErrorJsonGivenMissingDatabaseWhenPantsVerifyRuns()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "missing-db");
        var expected = LoadErrorGolden(
            "storage_error_v1.json",
            $"Storage path '{path}' does not exist.");

        var output = await RunVerifyAsync("verify", "--json", path);

        Assert.Equal(3, output.ExitCode);
        AssertJsonEqual(expected, output.StandardOutput);
        Assert.Empty(output.StandardError);
    }

    [Fact]
    public async Task ShouldEmitV1UsageErrorJsonGivenMissingPathWhenPantsVerifyRuns()
    {
        var expected = LoadGolden("usage_error_v1.json");
        expected["message"] = "usage: pants verify [--json] <db-path>";

        var output = await RunVerifyAsync("verify", "--json");

        Assert.Equal(2, output.ExitCode);
        AssertJsonEqual(expected, output.StandardOutput);
        Assert.Empty(output.StandardError);
    }

    [Fact]
    public async Task ShouldRejectPopulatedV2FixtureGivenUnsupportedFormatWhenPantsVerifyRuns()
    {
        using var directory = MidgeCompatibilityFixture.CopyToTemporaryDirectory(
            "v2_compressed_sst_db");
        var formatPath = Path.Combine(directory.Path, "FORMAT");
        var before = await File.ReadAllBytesAsync(formatPath);
        var expected = LoadErrorGolden(
            "corruption_error_v1.json",
            "The path does not contain a valid Midge FORMAT v3 marker.");

        var output = await RunVerifyAsync("verify", "--json", directory.Path);

        Assert.Equal(4, output.ExitCode);
        AssertJsonEqual(expected, output.StandardOutput);
        Assert.Equal(before, await File.ReadAllBytesAsync(formatPath));
        Assert.Empty(output.StandardError);
    }

    [Fact]
    public async Task ShouldVerifyCandidateWrittenDatabaseWhenPantsCliRuns()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put("release/key"u8.ToArray(), "release/value"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(family);
            await database.ShutdownAsync(TimeSpan.FromSeconds(30));
        }

        var output = await RunVerifyAsync("verify", "--json", directory.Path);
        var report = JsonNode.Parse(output.StandardOutput)!;

        Assert.Equal(0, output.ExitCode);
        Assert.Equal(1, (int?)report["schema_version"]);
        Assert.Equal("Healthy", (string?)report["health"]);
        Assert.Equal(1, (int?)report["sst_files_verified"]);
        Assert.Equal(true, (bool?)report["authoritative"]);
        Assert.Empty(output.StandardError);
    }

    static async Task<CliOutput> RunVerifyAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(typeof(PantsCliCommandLineParser).Assembly.Location);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        return new(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    static JsonNode LoadGolden(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, VerificationFixturePath, name)))!;

    static JsonNode LoadErrorGolden(string name, string message)
    {
        var path = Path.Combine(AppContext.BaseDirectory, VerificationFixturePath, name);
        var contents = File.ReadAllText(path);
        return JsonNode.Parse(
            contents.Replace("{{message}}", JsonSerializer.Serialize(message), StringComparison.Ordinal))!;
    }

    static void AssertJsonEqual(JsonNode expected, string actualJson)
    {
        var actual = JsonNode.Parse(actualJson);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Expected JSON:\n{expected.ToJsonString()}\nActual JSON:\n{actual?.ToJsonString()}");
    }

    sealed record CliOutput(int ExitCode, string StandardOutput, string StandardError);
}
