using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        Assert.Equal(10_000, result.OperationsPerInvoke);
        Assert.Equal("cold", result.MeasurementClass);
    }

    [Fact]
    public void ShouldIgnoreDerivedAllocationRatioGivenBenchmarkDotNetCsvRows()
    {
        const string csv = "Method,Job,StorageMode,Mean,Error,Allocated,Alloc Ratio\n" +
                           "BeginReadOnlyTransactionAsync,Dry,Local,10 ns,NA,1 KB,1.00\n" +
                           "BeginReadOnlyTransactionAsync,Dry,SimulatedCloud,12 ns,NA,2 KB,2.00\n";

        var results = BenchmarkCsvReader.Read(
            csv,
            "Cntryl.Pants.Tier3.MvccSystemBenchmarks",
            new Dictionary<string, int> { ["BeginReadOnlyTransactionAsync"] = 1 });

        Assert.Equal(2, results.Count);
        Assert.Equal(
            "pants:Cntryl.Pants.Tier3.MvccSystemBenchmarks:BeginReadOnlyTransactionAsync:StorageMode=Local",
            results[0].ScenarioId);
        Assert.Equal(1_024, results[0].AllocatedBytes);
        Assert.Equal(
            "pants:Cntryl.Pants.Tier3.MvccSystemBenchmarks:BeginReadOnlyTransactionAsync:StorageMode=SimulatedCloud",
            results[1].ScenarioId);
        Assert.Equal(2_048, results[1].AllocatedBytes);
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
        Assert.Equal(163, BenchmarkInventory.DiscoverScenarioIds().Count);
        Assert.Equal(163, BenchmarkInventory.DiscoverScenarioIds().Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ShouldReadMidgeSummaryGivenConfiguredSourcePin()
    {
        var json = MidgeJson(TestMidgeSourceSha);

        var result = Assert.Single(MidgeBenchmarkReader.Read(json, TestPin()));

        Assert.Equal("Tier2", result.Tier);
        Assert.Equal("midge:tier2/read", result.ScenarioId);
        Assert.Equal(125.5, result.Mean);
        Assert.Equal((ulong)300, result.OperationsCompleted);
        Assert.Equal("ns_per_op", result.PrimaryMetric);
        Assert.Equal("test cpu", result.Cpu);
    }

    [Fact]
    public void ShouldRejectMidgeArtifactGivenSourceShaDiffersFromConfiguredPin()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            MidgeBenchmarkReader.Read(MidgeJson(TestMidgeSourceSha), TestPin(new string('0', 40))));

        Assert.Contains("configured pin", exception.Message);
    }

    [Fact]
    public void ShouldRejectMidgeArtifactGivenStressToolVersionDiffersFromConfiguredPin()
    {
        var json = MidgeJson(TestMidgeSourceSha)
            .Replace("\"tool_version\": \"0.4.0\"", "\"tool_version\": \"0.3.0\"", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => MidgeBenchmarkReader.Read(json, TestPin()));

        Assert.Contains("does not match configured pin version", exception.Message);
    }

    [Fact]
    public void ShouldRejectMidgeCargoLockGivenHarnessRevisionIsNotPinned()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            MidgeBenchmarkReader.ValidateCargoLock("not a pinned Cargo.lock"u8.ToArray(), TestPin()));

        Assert.Contains("configured pin", exception.Message);
    }

    [Fact]
    public void ShouldRejectMidgeArtifactGivenMeasuredSampleCountDoesNotMatchSummary()
    {
        var json = MidgeJson(TestMidgeSourceSha)
            .Replace("\"measured_samples\": 2", "\"measured_samples\": 3", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => MidgeBenchmarkReader.Read(json, TestPin()));

        Assert.Contains("summary reports 3 measured samples", exception.Message);
    }

    [Fact]
    public void ShouldKeepMidgeScenarioIdentityStableGivenDiagnosticParametersChange()
    {
        var result = Assert.Single(MidgeBenchmarkReader.Read(MidgeJson(
            TestMidgeSourceSha,
            parametersJson: "{ \"candidate_blocks_checked_delta\": \"2813\", \"storage\": \"local\" }"), TestPin()));

        Assert.Equal("midge:tier2/read", result.ScenarioId);
        Assert.Contains("candidate_blocks_checked_delta=2813", result.Parameters);
    }

    [Fact]
    public void ShouldRejectMidgeFixtureGivenTier3SetupMarkerIsMissing()
    {
        var exception = Assert.Throws<InvalidDataException>(() => MidgeBenchmarkReader.Read(MidgeJson(
            TestMidgeSourceSha,
            name: "tier3_engine_get_local"), TestPin()));

        Assert.Contains("fixture memtable marker", exception.Message);
    }

    [Theory]
    [InlineData("tier3_engine_get_local", "2097152")]
    [InlineData("tier3_sst_point_seek_local", "4194304")]
    [InlineData("tier4_recovery_reopen_after_flush_local", "2097152")]
    public void ShouldReadMidgeFixtureGivenPinnedMemtableMarker(string name, string sizeBytes)
    {
        var result = Assert.Single(MidgeBenchmarkReader.Read(MidgeJson(
            TestMidgeSourceSha,
            name,
            $"{{ \"fixture_memtable_size_bytes\": \"{sizeBytes}\" }}",
            tier: 3), TestPin()));

        Assert.Equal(name, result.Workload);
        Assert.Contains($"fixture_memtable_size_bytes={sizeBytes}", result.Parameters);
    }

    [Fact]
    public void ShouldRejectIncompleteMidgeArtifactGivenPinnedScenarioInventory()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            MidgeBenchmarkReader.ValidateScenarioInventory(
                ["midge:tier2/read"],
                new HashSet<string>(["midge:tier2/expected"], StringComparer.Ordinal)));

        Assert.Contains("do not match the configured inventory", exception.Message);
        Assert.Contains("Missing:", exception.Message);
    }

    [Fact]
    public void ShouldReadMidgeArtifactsGivenConfiguredReleasePin()
    {
        using var directory = new TemporaryDirectory();
        var artifacts = Path.Combine(directory.Path, "artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "latest.json"), MidgeJson(TestMidgeSourceSha));
        File.WriteAllBytes(Path.Combine(artifacts, "Cargo.lock"), TestCargoLockBytes);
        var pinPath = WritePin(directory.Path, TestMidgeSourceSha, ["midge:tier2/read"]);

        var results = BenchmarkReportCommand.ReadMidge(artifacts, MidgeBenchmarkPin.Load(pinPath));

        var result = Assert.Single(results);
        Assert.Equal(TestMidgeSourceSha, result.SourceSha);
        Assert.Equal(TestPin().CargoLockSha256, result.CargoLockSha256);
    }

    [Fact]
    public void ShouldRejectMidgePinGivenScenarioInventoryBytesDifferFromConfiguredHash()
    {
        using var directory = new TemporaryDirectory();
        var pinPath = WritePin(directory.Path, TestMidgeSourceSha, ["midge:tier2/read"]);
        File.AppendAllText(Path.Combine(directory.Path, "scenarios.txt"), "midge:tier2/extra\n");

        var exception = Assert.Throws<InvalidDataException>(() => MidgeBenchmarkPin.Load(pinPath));

        Assert.Contains("does not match the configured pin", exception.Message);
    }

    [Fact]
    public void ShouldRejectMidgeArtifactsGivenSourceShaDiffersFromConfiguredReleasePin()
    {
        using var directory = new TemporaryDirectory();
        var artifacts = Path.Combine(directory.Path, "artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "latest.json"), MidgeJson(TestMidgeSourceSha));
        File.WriteAllBytes(Path.Combine(artifacts, "Cargo.lock"), TestCargoLockBytes);
        var pinPath = WritePin(directory.Path, new string('0', 40), ["midge:tier2/read"]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            BenchmarkReportCommand.ReadMidge(artifacts, MidgeBenchmarkPin.Load(pinPath)));

        Assert.Contains("does not match configured pin", exception.Message);
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
        File.WriteAllLines(
            Path.Combine(directory.Path, "pants-scenarios.txt"),
            Enumerable.Range(0, 151)
                .Select(index =>
                    $"Cntryl.Pants.Tier4.YcsbAWorkloadBenchmarks.RunAsync:[Scenario=Local-{index}]"));

        var exception = Assert.Throws<InvalidDataException>(() => BenchmarkReportCommand.ReadPants(directory.Path));

        Assert.Contains("expected 151 rows, found 0", exception.Message);
    }

    [Fact]
    public void ShouldRejectUnexpectedScenarioGivenItReplacesAnExpectedUniqueIdentity()
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
              "ExpectedScenarioCount": 2
            }
            """);
        File.WriteAllLines(
            Path.Combine(directory.Path, "pants-scenarios.txt"),
            [
                "Cntryl.Pants.Tier4.YcsbAWorkloadBenchmarks.RunAsync:[Scenario=Local-1]",
                "Cntryl.Pants.Tier4.YcsbAWorkloadBenchmarks.RunAsync:[Scenario=Local-2]"
            ]);
        File.WriteAllText(
            Path.Combine(directory.Path, "Cntryl.Pants.Tier4.YcsbAWorkloadBenchmarks-report.csv"),
            "Method,Job,Scenario,Mean,Error,Allocated\n" +
            "RunAsync,Dry,Local-1,1 ms,NA,1 KB\n" +
            "RunAsync,Dry,Local-3,1 ms,NA,1 KB\n");

        var exception = Assert.Throws<InvalidDataException>(() => BenchmarkReportCommand.ReadPants(directory.Path));

        Assert.Contains("Scenario=Local-2", exception.Message);
        Assert.Contains("Scenario=Local-3", exception.Message);
    }

    [Fact]
    public void ShouldRejectDirtyWorktreeBeforeCapturingBenchmarkSourceRevision()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BenchmarkEnvironment.EnsureCleanWorkingTree(" M bench/Pants.Benches/Program.cs"));

        Assert.Contains("worktree must be clean", exception.Message);
    }

    [Fact]
    public void ShouldAcceptCleanWorktreeForBenchmarkSourceRevision() =>
        BenchmarkEnvironment.EnsureCleanWorkingTree(string.Empty);

    [Fact]
    public void ShouldAvoidUnsupportedRatioGivenRenderingCrossEngineReport()
    {
        var metadata = new BenchmarkRunMetadata(
            "pants", "pants-sha", "test-os", "Arm64", "test-cpu", ".NET test", "cold", 1);
        var pants = new BenchmarkResult("pants:id", "Tier2", "Read", "clients=16", 1, 100, null, 10, "cold");
        var pin = TestPin();
        var midge = Assert.Single(MidgeBenchmarkReader.Read(MidgeJson(TestMidgeSourceSha), pin)) with
        {
            CargoLockSha256 = pin.CargoLockSha256
        };

        var report = BenchmarkReportCommand.Render(metadata, [pants], [midge], pin);

        Assert.Contains("not treated as equivalent", report);
        Assert.Contains("Logical ops/invoke", report);
        Assert.Contains("Measured ops", report);
        Assert.Contains("Midge release: `0.1.1`", report);
        Assert.Contains(TestMidgeSourceSha, report);
        Assert.Contains(pin.CargoLockSha256, report);
        Assert.Contains(pin.ScenarioInventorySha256, report);
        Assert.Contains("| Read | clients=16 | 1 | 100.00 |", report);
        Assert.Contains("| point read | clients=16;storage=local | smoke | ns_per_op | 2 | 300 |", report);
        Assert.DoesNotContain("| Ratio |", report, StringComparison.OrdinalIgnoreCase);
    }

    static string MidgeJson(
        string sourceSha,
        string name = "point read",
        string parametersJson = "{ \"storage\": \"local\", \"clients\": \"16\" }",
        int tier = 2) => $$"""
                                                   {
                                                     "schema_version": "cntryl-stress.v2",
                                                     "tool_version": "0.4.0",
                                                     "run_profile": "smoke",
                                                     "environment": {
                                                       "git_commit": "{{sourceSha}}",
                                                       "cpu_model": "test cpu",
                                                       "os": "test os",
                                                       "rustc_version": "rustc test"
                                                     },
                                                     "summaries": [
                                                       {
                                                         "benchmark_id": "tier2/read",
                                                         "name": "{{name}}",
                                                         "tier": {{tier}},
                                                         "measured_samples": 2,
                                                         "primary_metric": "ns_per_op",
                                                         "stats": { "mean": 125.5 },
                                                         "quality": "acceptable",
                                                         "trust_class": "gate",
                                                         "correctness": { "passed": true },
                                                         "parameters": {{parametersJson}}
                                                       }
                                                     ],
                                                     "samples": [
                                                       { "benchmark_id": "tier2/read", "phase": "warmup", "operations_completed": 9 },
                                                       { "benchmark_id": "tier2/read", "phase": "measured", "operations_completed": 100 },
                                                       { "benchmark_id": "tier2/read", "phase": "measured", "operations_completed": 200 },
                                                       { "benchmark_id": "tier2/read", "phase": "cooldown", "operations_completed": 8 }
                                                     ]
                                                   }
                                                   """;

    const string TestMidgeSourceSha = "7d39f86217fdb07191a83bd885a514dd5ea9723f";

    static readonly byte[] TestCargoLockBytes = Encoding.UTF8.GetBytes(
        """
        [[package]]
        name = "cntryl-stress"
        version = "0.4.0"
        source = "registry+https://github.com/rust-lang/crates.io-index"
        checksum = "61338fa3caeb07817598b9c7080ba4d94e9961352467226633c707581f545b07"
        """);

    static MidgeBenchmarkPin TestPin(string? sourceSha = null)
    {
        var inventory = Encoding.UTF8.GetBytes("midge:tier2/read\n");
        return new MidgeBenchmarkPin(
            "0.1.1",
            sourceSha ?? TestMidgeSourceSha,
            "0.4.0",
            Hash(TestCargoLockBytes),
            Hash(inventory),
            new HashSet<string>(["midge:tier2/read"], StringComparer.Ordinal));
    }

    static string WritePin(string directory, string sourceSha, IReadOnlyList<string> scenarioIds)
    {
        var inventoryBytes = Encoding.UTF8.GetBytes(string.Join('\n', scenarioIds) + "\n");
        var inventoryPath = Path.Combine(directory, "scenarios.txt");
        File.WriteAllBytes(inventoryPath, inventoryBytes);
        var pinPath = Path.Combine(directory, "midge-pin.json");
        File.WriteAllText(pinPath, JsonSerializer.Serialize(new
        {
            release = "0.1.1",
            source_sha = sourceSha,
            stress_tool_version = "0.4.0",
            cargo_lock_sha256 = Hash(TestCargoLockBytes),
            scenario_inventory_file = Path.GetFileName(inventoryPath),
            scenario_inventory_sha256 = Hash(inventoryBytes)
        }));
        return pinPath;
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
