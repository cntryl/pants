using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pants.Tests;

[Collection(CrashProcessTestGroup.Name)]
public sealed class PantsCloudCrashRecoveryTests
{
    const string ChildScenarioEnvironmentVariable = "PANTS_CLOUD_CRASH_SCENARIO";
    const string DatabasePathEnvironmentVariable = "PANTS_CLOUD_CRASH_DATABASE_PATH";
    const string ActiveWalScenario = "active-wal";
    const string LocalWalScenario = "local-wal";
    const string CloudStrictScenario = "cloud-strict";
    const string PublishedSstScenario = "published-sst";
    const string ReadyFileName = "cloud-crash-child.ready";

    [Fact]
    public async Task ShouldAbortGivenCloudCrashChildScenario()
    {
        var scenario = Environment.GetEnvironmentVariable(
            ChildScenarioEnvironmentVariable);
        if (!IsKnownScenario(scenario))
        {
            return;
        }

        var path = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable));
        var options = CreateOptions(
            path,
            buffered: StringComparer.Ordinal.Equals(scenario, PublishedSstScenario) ||
                StringComparer.Ordinal.Equals(scenario, LocalWalScenario));
        var dependencies = StringComparer.Ordinal.Equals(scenario, LocalWalScenario)
            ? new PantsRuntimeDependencies(
                new CrashChildFailpointHandler(PantsFailpoint.BeforeCloudWalUpload))
            : PantsRuntimeDependencies.Default;
        await using var database = await PantsDatabase.OpenForTestingAsync(options, dependencies);

        if (StringComparer.Ordinal.Equals(scenario, PublishedSstScenario))
        {
            await PreparePublishedSstScenarioAsync(database);
        }
        else
        {
            await CommitAsync(
                database,
                GetScenarioKey(scenario),
                "crash-value",
                StringComparer.Ordinal.Equals(scenario, CloudStrictScenario)
                    ? PantsWriteOptions.CloudStrict
                    : PantsWriteOptions.CloudAsync);
        }

        if (StringComparer.Ordinal.Equals(scenario, LocalWalScenario))
        {
            await WaitForMetricsAsync(
                database,
                static candidate =>
                    candidate.CurrentSequence >= 1 &&
                    candidate.WalCloudDurableSequence < candidate.CurrentSequence);
            Assert.NotEmpty(Directory.EnumerateFiles(
                Path.Combine(path, "wal"),
                "*.wal",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(path, "cloud_store", "wal"),
                "*.wal",
                SearchOption.AllDirectories));
        }

        await PublishSignalAsync(Path.Combine(path, ReadyFileName), "ready");

        using var process = Process.GetCurrentProcess();
        try
        {
            process.Kill();
        }
        finally
        {
            Environment.Exit(137);
        }
    }

    [Fact]
    public async Task ShouldResumeCloudUploadFromRecoveredActiveWalAfterChildAbort()
    {
        using var directory = new TemporaryDirectory();
        using var child = StartCrashChild(directory.Path, ActiveWalScenario);
        try
        {
            await WaitForChildReadinessAsync(child, directory.Path);
        }
        finally
        {
            await TerminateCrashChildAsync(child, directory.Path);
        }

        await ExpireCrashedProcessLeaseAsync(directory.Path);

        var activeWal = Path.Combine(directory.Path, "wal", "wal.log");
        Assert.True(new FileInfo(activeWal).Length > 0);

        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        var metrics = await WaitForMetricsAsync(
            reopened,
            static candidate =>
                candidate.CurrentSequence >= 1 &&
                candidate.WalCloudDurableSequence >= candidate.CurrentSequence);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            "crash-value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync(TestBytes.FromString(GetScenarioKey(ActiveWalScenario))))));
        Assert.True(metrics.WalCloudDurableSequence >= metrics.CurrentSequence);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cloud_store", "wal"),
            "*.wal",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ShouldKeepWalSegmentsEpochScopedWhenCloudStrictCommitFollowsRecoveredActiveWal()
    {
        using var directory = new TemporaryDirectory();
        using var child = StartCrashChild(directory.Path, ActiveWalScenario);
        try
        {
            await WaitForChildReadinessAsync(child, directory.Path);
        }
        finally
        {
            await TerminateCrashChildAsync(child, directory.Path);
        }

        await ExpireCrashedProcessLeaseAsync(directory.Path);

        var activeWal = Path.Combine(directory.Path, "wal", "wal.log");
        Assert.True(new FileInfo(activeWal).Length > 0);
        var crashedWriterEpoch = AssertSingleWriterEpoch(activeWal);

        await using (var reopened = await PantsDatabase.OpenAsync(CreateOptions(directory.Path)))
        {
            await CommitAsync(
                reopened,
                "cloud-strict-after-reopen-key",
                "cloud-strict-after-reopen-value",
                PantsWriteOptions.CloudStrict);
        }

        await using var verified = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        await AssertValueAsync(
            verified,
            GetScenarioKey(ActiveWalScenario),
            "crash-value");
        await AssertValueAsync(
            verified,
            "cloud-strict-after-reopen-key",
            "cloud-strict-after-reopen-value");

        var localWalPaths = Directory.EnumerateFiles(
            Path.Combine(directory.Path, "wal"),
            "*.wal",
            SearchOption.TopDirectoryOnly);
        var publishedWalPaths = Directory.EnumerateFiles(
            Path.Combine(directory.Path, "cloud_store", "wal"),
            "*.wal",
            SearchOption.AllDirectories);
        var sealedWalPaths = localWalPaths.Concat(publishedWalPaths).ToArray();
        Assert.NotEmpty(sealedWalPaths);

        var writerEpochs = sealedWalPaths
            .Select(AssertSingleWriterEpoch)
            .Distinct()
            .ToArray();
        Assert.Contains(crashedWriterEpoch, writerEpochs);
        Assert.Equal(2, writerEpochs.Length);
    }

    [Fact]
    public async Task ShouldRecoverLocalCloudAsyncWalWhenChildAbortsBeforeUpload()
    {
        using var directory = new TemporaryDirectory();
        using var child = StartCrashChild(directory.Path, LocalWalScenario);
        try
        {
            await WaitForChildReadinessAsync(child, directory.Path);
        }
        finally
        {
            await TerminateCrashChildAsync(child, directory.Path);
        }

        await ExpireCrashedProcessLeaseAsync(directory.Path);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "wal"),
            "*.wal",
            SearchOption.TopDirectoryOnly));

        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));

        await AssertValueAsync(reopened, GetScenarioKey(LocalWalScenario), "crash-value");
    }

    [Fact]
    public async Task ShouldRecoverCloudStrictWriteWhenCacheLostAfterChildAbort()
    {
        using var directory = new TemporaryDirectory();
        using var child = StartCrashChild(directory.Path, CloudStrictScenario);
        try
        {
            await WaitForChildReadinessAsync(child, directory.Path);
        }
        finally
        {
            await TerminateCrashChildAsync(child, directory.Path);
        }

        RemoveLocalCache(directory.Path);

        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            "crash-value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync(TestBytes.FromString(GetScenarioKey(CloudStrictScenario))))));
    }

    [Fact]
    public async Task ShouldRestorePublishedCloudSstWhenCacheLostAfterChildAbort()
    {
        using var directory = new TemporaryDirectory();
        using var child = StartCrashChild(directory.Path, PublishedSstScenario);
        try
        {
            await WaitForChildReadinessAsync(child, directory.Path);
        }
        finally
        {
            await TerminateCrashChildAsync(child, directory.Path);
        }

        await ExpireCrashedProcessLeaseAsync(directory.Path);
        ResetDirectory(Path.Combine(directory.Path, "wal"));
        ResetDirectory(Path.Combine(directory.Path, "sst"));

        await using var reopened = await PantsDatabase.OpenAsync(
            CreateOptions(directory.Path, buffered: true));
        var metrics = await reopened.GetRuntimeMetricsAsync();
        var layout = await reopened.GetStorageLayoutAsync();

        Assert.True(metrics.SstCount >= 1);
        Assert.True(metrics.ManifestLastPersistedSequence > 0);
        Assert.True(layout.Levels.Sum(static level => level.FileCount) >= 1);
        for (var index = 0; index < 17; index++)
        {
            await AssertValueAsync(
                reopened,
                $"cloud-buffered-crash-key-{index:0000}",
                "cloud-buffered-crash-value");
        }
    }

    [Fact]
    public async Task ShouldFailStrictButSalvageValidPrefixGivenCorruptRemoteWalSegment()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, "prefix-key", "prefix-value", PantsWriteOptions.CloudStrict);
            await CommitAsync(database, "truncated-key", "truncated-value", PantsWriteOptions.CloudStrict);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        var remoteWal = Directory.EnumerateFiles(
                Path.Combine(directory.Path, "cloud_store", "wal"),
                "*.wal",
                SearchOption.AllDirectories)
            .Max(StringComparer.Ordinal);
        Assert.NotNull(remoteWal);
        var corruptBytes = await File.ReadAllBytesAsync(remoteWal);
        await File.WriteAllBytesAsync(remoteWal, corruptBytes[..^1]);
        var retainedBytes = await File.ReadAllBytesAsync(remoteWal);
        ResetDirectory(Path.Combine(directory.Path, "wal"));

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(
            () => PantsDatabase.OpenAsync(options).AsTask());

        await using var salvaged = await PantsDatabase.OpenAsync(
            options.WithRecoveryPolicy(PantsRecoveryPolicy.Salvage));
        var metrics = await salvaged.GetRuntimeMetricsAsync();

        Assert.Equal(PantsEngineHealth.SalvageMode, metrics.Health);
        await AssertValueAsync(salvaged, "prefix-key", "prefix-value");
        await AssertMissingAsync(salvaged, "truncated-key");
        Assert.Equal(retainedBytes, await File.ReadAllBytesAsync(remoteWal));
    }

    static Process StartCrashChild(string databasePath, string scenario)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                Environment.ProcessPath ??
                "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(PantsCloudCrashRecoveryTests).Assembly.Location);
        start.ArgumentList.Add($"/Platform:{RuntimeInformation.ProcessArchitecture}");
        start.ArgumentList.Add(
            $"--Tests:{typeof(PantsCloudCrashRecoveryTests).FullName}.ShouldAbortGivenCloudCrashChildScenario");
        start.Environment[ChildScenarioEnvironmentVariable] = scenario;
        start.Environment[DatabasePathEnvironmentVariable] = databasePath;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start crash child.");
    }

    static async Task WaitForChildReadinessAsync(Process child, string databasePath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var readyPath = Path.Combine(databasePath, ReadyFileName);
        while (!File.Exists(readyPath))
        {
            if (child.HasExited)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Crash child exited with code {child.ExitCode} before readiness.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    static async Task TerminateCrashChildAsync(Process child, string databasePath)
    {
        await WaitForCrashChildExitAsync(child);
        await WaitForCrashChildLockReleaseAsync(databasePath);
    }

    static async Task WaitForCrashChildExitAsync(Process child)
    {
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(exitTimeout.Token);
        }
        catch (OperationCanceledException exception) when (exitTimeout.IsCancellationRequested)
        {
            TryKill(child);
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await child.WaitForExitAsync(cleanupTimeout.Token);
            }
            catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
            {
                // The failure below reports the bounded cleanup failure.
            }

            throw new Xunit.Sdk.XunitException(
                "Crash child did not exit within 10 seconds after signaling readiness.",
                exception);
        }
    }

    static async Task WaitForCrashChildLockReleaseAsync(string databasePath)
    {
        var lockPath = Path.Combine(databasePath, "LOCK");
        if (!File.Exists(lockPath))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
    }

    static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited after HasExited was observed.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The timeout remains the primary failure when cleanup cannot signal the process.
        }
    }

    static async Task PublishSignalAsync(string path, string contents)
    {
        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temporaryPath, contents);
        File.Move(temporaryPath, path);
    }

    static async Task ExpireCrashedProcessLeaseAsync(string databasePath)
    {
        var leasePath = Path.Combine(databasePath, ".midge_leader");
        var lines = await File.ReadAllLinesAsync(leasePath);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("acquired_at: ", StringComparison.Ordinal))
            {
                lines[index] = "acquired_at: 1970-01-01T00:00:00Z";
            }
        }

        await File.WriteAllLinesAsync(leasePath, lines);
        File.Delete(Path.Combine(databasePath, ".midge_leader.lock"));
    }

    static void RemoveLocalCache(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (Path.GetFileName(path) == "cloud_store")
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    static async Task<PantsRuntimeMetrics> WaitForMetricsAsync(
        IPantsDatabase database,
        Func<PantsRuntimeMetrics, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        PantsRuntimeMetrics? last = null;
        try
        {
            while (true)
            {
                last = await database.GetRuntimeMetricsAsync(timeout.Token);
                if (predicate(last))
                {
                    return last;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException(
                $"Cloud crash scenario metrics did not converge: " +
                $"sequence={last?.CurrentSequence}, cloud={last?.WalCloudDurableSequence}, " +
                $"sst={last?.SstCount}, persisted={last?.ManifestLastPersistedSequence}, " +
                $"walSegment={last?.WalCurrentSegmentId}, pending={last?.WalPendingWrites}, " +
                $"uploads={last?.PendingCloudUploads}, " +
                $"health={last?.Health}.");
        }
    }

    static async Task PreparePublishedSstScenarioAsync(IPantsDatabase database)
    {
        for (var index = 0; index < 16; index++)
        {
            await CommitAsync(
                database,
                $"cloud-buffered-crash-key-{index:0000}",
                "cloud-buffered-crash-value",
                PantsWriteOptions.CloudAsync);
        }

        await WaitForMetricsAsync(
            database,
            static candidate =>
                candidate.SstCount >= 1 &&
                candidate.ManifestLastPersistedSequence > 0);
        await CommitAsync(
            database,
            "cloud-buffered-crash-key-0016",
            "cloud-buffered-crash-value",
            PantsWriteOptions.CloudAsync);
        await WaitForMetricsAsync(
            database,
            static candidate =>
                candidate.CurrentSequence >= 17 &&
                candidate.WalCloudDurableSequence >= candidate.CurrentSequence);
    }

    static async Task CommitAsync(
        IPantsDatabase database,
        string key,
        string value,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(writeOptions);
    }

    static async Task AssertValueAsync(
        IPantsDatabase database,
        string key,
        string expected)
    {
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync(TestBytes.FromString(key)));
        Assert.Equal(expected, TestBytes.ToText(value));
    }

    static async Task AssertMissingAsync(IPantsDatabase database, string key)
    {
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString(key)));
    }

    static ulong AssertSingleWriterEpoch(string path)
    {
        var writerEpochs = new HashSet<ulong>();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        MidgeWalFrameReader.Visit(
            stream,
            (record, _) => writerEpochs.Add(record.WriterEpoch));
        return Assert.Single(writerEpochs);
    }

    static bool IsKnownScenario(string? scenario) =>
        StringComparer.Ordinal.Equals(scenario, ActiveWalScenario) ||
        StringComparer.Ordinal.Equals(scenario, LocalWalScenario) ||
        StringComparer.Ordinal.Equals(scenario, CloudStrictScenario) ||
        StringComparer.Ordinal.Equals(scenario, PublishedSstScenario);

    static string GetScenarioKey(string? scenario) => scenario switch
    {
        ActiveWalScenario => "cloud-async-active-crash-key",
        LocalWalScenario => "cloud-async-local-crash-key",
        CloudStrictScenario => "cloud-strict-crash-key",
        _ => throw new InvalidOperationException($"Unknown crash scenario '{scenario}'.")
    };

    static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    static PantsOpenOptions CreateOptions(string path, bool buffered = false) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "cloud-crash/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                EventualFlushSegmentGap: buffered ? 4 : int.MaxValue,
                WalSealMinimumSegmentBytes: long.MaxValue,
                WalSealMaximumFlushDelay: TimeSpan.FromHours(1),
                WalSealMaximumPendingWrites: buffered ? 1 : int.MaxValue))
            .WithBackgroundCompaction(false);
}
