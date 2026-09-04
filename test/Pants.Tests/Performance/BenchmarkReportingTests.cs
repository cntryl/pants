using Cntryl.Pants.Reporting;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Performance;

public sealed class BenchmarkReportingTests
{
    [Theory]
    [InlineData("12 ns", 12)]
    [InlineData("1.5 us", 1_500)]
    [InlineData("1.5 μs", 1_500)]
    [InlineData("2 ms", 2_000_000)]
    [InlineData("0.25 s", 250_000_000)]
    public void ShouldNormalizeTimeGivenSupportedBenchmarkUnit(string value, double expected) =>
        Assert.Equal(expected, BenchmarkUnitParser.ParseTimeNanoseconds(value));

    [Theory]
    [InlineData("12 B", 12)]
    [InlineData("1.5 KB", 1_536)]
    [InlineData("2 MB", 2_097_152)]
    [InlineData("0.5 GB", 536_870_912)]
    public void ShouldNormalizeAllocationGivenSupportedBenchmarkUnit(string value, double expected) =>
        Assert.Equal(expected, BenchmarkUnitParser.ParseBytes(value));

    [Fact]
    public void ShouldParseQuotedThousandsAndMissingErrorGivenBenchmarkCsvRow()
    {
        const string csv = "Method,Job,Scenario,Mean,Error,Allocated\n" +
                           "RunAsync,Dry,Local-16,\"3,239.7 μs\",NA,2.88 MB\n";

        var result = Assert.Single(BenchmarkCsvReader.Read(
            csv,
            "Cntryl.Pants.Tier4.YcsbAWorkloadBenchmarks",
            new Dictionary<string, int> { ["RunAsync"] = 10_000 }));

        Assert.Equal(3_239_700, result.MeanNanoseconds);
        Assert.Null(result.ErrorNanoseconds);
        Assert.Equal(3_019_898.88, result.AllocatedBytes, 2);
        Assert.Equal("Scenario=Local-16", result.Parameters);
        Assert.Equal("cold", result.MeasurementClass);
    }

    [Fact]
    public void ShouldRejectDuplicateIdentityGivenRepeatedBenchmarkRow()
    {
        const string row = "RunAsync,Dry,Local-1,1 ms,NA,1 KB\n";
        const string csv = "Method,Job,Scenario,Mean,Error,Allocated\n" + row + row;

        var exception = Assert.Throws<InvalidDataException>(() => BenchmarkCsvReader.Read(
            csv,
            "Cntryl.Pants.Tier4.YcsbAWorkloadBenchmarks",
            new Dictionary<string, int> { ["RunAsync"] = 10_000 }));

        Assert.Contains("Duplicate benchmark scenario", exception.Message);
    }

    [Fact]
    public void ShouldRejectMissingMeanGivenIncompleteBenchmarkRow()
    {
        const string csv = "Method,Job,Scenario,Mean,Error,Allocated\nRunAsync,Dry,Local-1,NA,NA,1 KB\n";

        Assert.Throws<InvalidDataException>(() => BenchmarkCsvReader.Read(
            csv,
            "Cntryl.Pants.Tier4.YcsbAWorkloadBenchmarks",
            new Dictionary<string, int> { ["RunAsync"] = 10_000 }));
    }

    [Fact]
    public void ShouldDiscoverEveryParameterizedScenarioGivenPracticalInventory()
    {
        Assert.Equal(157, BenchmarkInventory.DiscoverScenarioIds().Count);
        Assert.Equal(157, BenchmarkInventory.DiscoverScenarioIds().Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ShouldReadPinnedMidgeSummaryGivenCurrentStressSchema()
    {
        var json = MidgeJson(MidgeBenchmarkReader.PinnedSourceSha);

        var result = Assert.Single(MidgeBenchmarkReader.Read(json));

        Assert.Equal("Tier2", result.Tier);
        Assert.Equal("midge:tier2/read:clients=16;storage=local", result.ScenarioId);
        Assert.Equal(125.5, result.Mean);
        Assert.Equal("ns_per_op", result.PrimaryMetric);
        Assert.Equal("test cpu", result.Cpu);
    }

    [Fact]
    public void ShouldRejectMidgeArtifactGivenSourceShaIsNotPinned()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            MidgeBenchmarkReader.Read(MidgeJson(new string('0', 40))));

        Assert.Contains("does not match pinned SHA", exception.Message);
    }

    [Fact]
    public void ShouldRejectIncompletePantsArtifactGivenExpectedScenarioCount()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "pants-metadata.json"),
            """
            {
              "Engine": "pants",
              "SourceSha": "0123456789abcdef",
              "OperatingSystem": "test",
              "Architecture": "Arm64",
              "Cpu": "test cpu",
              "Runtime": ".NET test",
              "MeasurementClass": "cold",
              "ExpectedScenarioCount": 151
            }
            """);

        var exception = Assert.Throws<InvalidDataException>(() => BenchmarkReportCommand.ReadPants(directory.Path));

        Assert.Contains("expected 151 rows, found 0", exception.Message);
    }

    [Fact]
    public void ShouldAvoidUnsupportedRatioGivenRenderingCrossEngineReport()
    {
        var metadata = new BenchmarkRunMetadata(
            "pants", "pants-sha", "test-os", "Arm64", "test-cpu", ".NET test", "cold", 1);
        var pants = new BenchmarkResult("pants:id", "Tier2", "Read", "clients=16", 1, 100, null, 10, "cold");
        var midge = Assert.Single(MidgeBenchmarkReader.Read(MidgeJson(MidgeBenchmarkReader.PinnedSourceSha)));

        var report = BenchmarkReportCommand.Render(metadata, [pants], [midge]);

        Assert.Contains("not treated as equivalent", report);
        Assert.DoesNotContain("| Ratio |", report, StringComparison.OrdinalIgnoreCase);
    }

    static string MidgeJson(string sourceSha) => $$"""
                                                   {
                                                     "schema_version": "cntryl-stress.v2",
                                                     "tool_version": "0.3.0",
                                                     "environment": {
                                                       "git_commit": "{{sourceSha}}",
                                                       "cpu_model": "test cpu",
                                                       "os": "test os",
                                                       "rustc_version": "rustc test"
                                                     },
                                                     "summaries": [
                                                       {
                                                         "benchmark_id": "tier2/read",
                                                         "name": "point read",
                                                         "tier": 2,
                                                         "primary_metric": "ns_per_op",
                                                         "stats": { "mean": 125.5 },
                                                         "quality": "acceptable",
                                                         "trust_class": "gate",
                                                         "correctness": { "passed": true },
                                                         "parameters": { "storage": "local", "clients": "16" }
                                                       }
                                                     ]
                                                   }
                                                   """;
}
