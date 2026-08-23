using System.Text.Json;

namespace Cntryl.Pants.Tests.Transactions.Spill;

public sealed class PantsTransactionSpillHardeningBehaviorTests
{
    const byte PutOperation = 0;
    const byte TransactionBeginOperation = 4;
    const byte TransactionCommitOperation = 5;
    const byte TransactionBatchOperation = 6;

    [Fact]
    public async Task ShouldBoundSharedResidentBytesWhenTwoTransactionsPressureOnePool()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(
            directory.Path,
            1_500);
        await using var first = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        await using var second = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        var firstValue = Enumerable.Repeat((byte)'a', 900).ToArray();
        var secondValue = Enumerable.Repeat((byte)'b', 900).ToArray();

        first.Put("first"u8.ToArray(), firstValue);
        second.Put("second"u8.ToArray(), secondValue);

        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        Assert.Equal(firstValue, (await first.GetAsync("first"u8.ToArray()))!.Value.ToArray());
        Assert.Equal(secondValue, (await second.GetAsync("second"u8.ToArray()))!.Value.ToArray());
        await first.RollbackAsync();
        await second.RollbackAsync();
    }

    [Fact]
    public async Task ShouldCreateEnginePrivateRunsUnderTxnWhenDurableTransactionSpills()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);

        TransactionSpillHardeningTestHarness.Fill(transaction, "durable", 4);

        var artifacts = TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path);
        Assert.NotEmpty(artifacts);
        Assert.All(artifacts, path => Assert.True(File.Exists(path)));
        Assert.All(
            artifacts,
            path => Assert.StartsWith(
                Path.GetFullPath(Path.Combine(directory.Path, "txn")) + Path.DirectorySeparatorChar,
                Path.GetFullPath(path),
                StringComparison.Ordinal));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ShouldReadLatestPointIntentAfterSpilling()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("point"u8.ToArray(), "before-spill"u8.ToArray());

        TransactionSpillHardeningTestHarness.Fill(transaction, "point-fill", 4);
        var beforeUpdate = await transaction.GetAsync("point"u8.ToArray());
        transaction.Put("point"u8.ToArray(), "after-spill"u8.ToArray());
        var afterUpdate = await transaction.GetAsync("point"u8.ToArray());

        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        Assert.Equal("before-spill", TestBytes.ToText(beforeUpdate!.Value));
        Assert.Equal("after-spill", TestBytes.ToText(afterUpdate!.Value));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ShouldMergeTransactionIntentsWhenScanningAfterSpill()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using (var seed = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            seed.Put("scan:base"u8.ToArray(), "snapshot"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("scan:a"u8.ToArray(), "resident-or-spilled-a"u8.ToArray());
        TransactionSpillHardeningTestHarness.Fill(transaction, "outside-prefix", 4);
        transaction.Put("scan:b"u8.ToArray(), "resident-or-spilled-b"u8.ToArray());
        transaction.Put("scan:base"u8.ToArray(), "overridden"u8.ToArray());

        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "scan:"u8.ToArray()
        });
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var entry in scan)
        {
            rows[TestBytes.ToText(entry.Key)] = TestBytes.ToText(entry.Value);
        }

        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        Assert.Equal("resident-or-spilled-a", rows["scan:a"]);
        Assert.Equal("resident-or-spilled-b", rows["scan:b"]);
        Assert.Equal("overridden", rows["scan:base"]);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ShouldReturnReverseRowsInDescendingOrderGivenPrefixAndLimitWhenScanning()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("scan:a"u8.ToArray(), "a"u8.ToArray());
        transaction.Put("scan:b"u8.ToArray(), "b"u8.ToArray());
        TransactionSpillHardeningTestHarness.Fill(transaction, "outside-prefix", 4);
        transaction.Put("scan:c"u8.ToArray(), "c"u8.ToArray());
        transaction.Put("scan:d"u8.ToArray(), "d"u8.ToArray());

        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "scan:"u8.ToArray(),
            Direction = PantsScanDirection.Reverse,
            Limit = 3
        });
        var rows = new List<string>();
        await using var enumerator = scan.GetAsyncEnumerator();
        while (await enumerator.MoveNextAsync())
        {
            rows.Add(TestBytes.ToText(enumerator.Current.Key));
        }

        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        Assert.True(scan.IsExhausted);
        Assert.False(scan.IsFailed);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(["scan:d", "scan:c", "scan:b"], rows);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ShouldPreserveTransactionAtomicityGivenSpillRunReadFailureWhenCommitting()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        TransactionSpillHardeningTestHarness.Fill(transaction, "atomic", 6);
        var dataRun = Assert.Single(
            TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path)
                .Where(static path => path.EndsWith(".run", StringComparison.Ordinal))
                .Take(1));
        File.Delete(dataRun);

        var error = await Assert.ThrowsAnyAsync<PantsException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Contains(error.Code, new[] { PantsErrorCode.Io, PantsErrorCode.Corruption });
        for (var index = 0; index < 6; index++)
        {
            Assert.Null(await TransactionSpillHardeningTestHarness.ReadTextAsync(
                database,
                $"atomic-{index:000}"));
        }
    }

    [Fact]
    public async Task ShouldPreservePutDeleteInsertOrdinalsAcrossSpillRuns()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("ordered"u8.ToArray(), "first"u8.ToArray());
        TransactionSpillHardeningTestHarness.Fill(transaction, "ordinal-a", 2);
        transaction.Delete("ordered"u8.ToArray());
        TransactionSpillHardeningTestHarness.Fill(transaction, "ordinal-b", 2);
        transaction.Insert("ordered"u8.ToArray(), "final"u8.ToArray());

        var beforeCommit = await transaction.GetAsync("ordered"u8.ToArray());
        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        await transaction.CommitAsync(PantsWriteOptions.Sync);

        Assert.Equal("final", TestBytes.ToText(beforeCommit!.Value));
        Assert.Equal(
            "final",
            await TransactionSpillHardeningTestHarness.ReadTextAsync(database, "ordered"));
    }

    [Fact]
    public async Task ShouldRejectDuplicateInsertWhenDuplicateIsInOlderSpillRun()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Insert("duplicate"u8.ToArray(), "first"u8.ToArray());
        TransactionSpillHardeningTestHarness.Fill(transaction, "duplicate-fill", 3);
        transaction.Insert("duplicate"u8.ToArray(), "second"u8.ToArray());

        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        Assert.Null(await TransactionSpillHardeningTestHarness.ReadTextAsync(database, "duplicate"));
    }

    [Fact]
    public async Task ShouldRemoveSpillRunsWhenTransactionRollsBack()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        TransactionSpillHardeningTestHarness.Fill(transaction, "rollback", 4);
        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));

        await transaction.RollbackAsync();

        Assert.Empty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
    }

    [Fact]
    public async Task ShouldRemoveSpillRunsWhenTransactionIsDropped()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        TransactionSpillHardeningTestHarness.Fill(transaction, "drop", 4);
        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));

        await transaction.DisposeAsync();

        Assert.Empty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
    }

    [Fact]
    public async Task ShouldReturnResourceLimitWhenMemoryModeExhaustsTransactionPool()
    {
        await using var database = await PantsDatabase.OpenAsync(
            TransactionSpillHardeningTestHarness.CreateMemoryOptions());
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        var value = Enumerable.Repeat((byte)'m', 8 * 1_024).ToArray();
        PantsException? resourceError = null;

        for (var index = 0; index < 8; index++)
        {
            try
            {
                transaction.Put(TestBytes.FromString($"memory-{index:00}"), value);
            }
            catch (PantsException exception)
            {
                resourceError = exception;
                break;
            }
        }

        var error = Assert.IsType<PantsResourceLimitException>(resourceError);
        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
    }

    [Fact]
    public async Task ShouldRemoveOrphanedSpillRunsWhenEngineStarts()
    {
        using var directory = new TemporaryDirectory();
        await using (var initialized = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path))
        {
        }

        var transactionDirectory = Path.Combine(directory.Path, "txn");
        Directory.CreateDirectory(transactionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(transactionDirectory, "orphan.run"),
            "uncommitted spill residue");

        await using var reopened = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);

        Assert.Empty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
    }

    [Fact]
    public async Task ShouldFrameLargeTransactionWithOneCommitMarker()
    {
        using var directory = new TemporaryDirectory();
        const int logicalValueBytes = 12 * 900;
        await using (var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            TransactionSpillHardeningTestHarness.Fill(transaction, "chunked", 12);

            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        var frames = TransactionSpillHardeningTestHarness.ReadWalFrames(directory.Path);

        Assert.Equal(TransactionBeginOperation, frames[0].Operation);
        Assert.Equal(1UL, frames[0].Sequence);
        Assert.Equal(TransactionCommitOperation, frames[^1].Operation);
        Assert.Equal(14UL, frames[^1].Sequence);
        Assert.Equal(
            Enumerable.Range(1, frames.Count).Select(static sequence => (ulong)sequence),
            frames.Select(static frame => frame.Sequence));
        Assert.True(frames.Count >= 4);
        Assert.DoesNotContain(frames, static frame => frame.Operation == TransactionBatchOperation);
        Assert.Equal(1, frames.Count(static frame => frame.Operation == TransactionCommitOperation));
        Assert.All(frames, frame => Assert.True(frame.PayloadLength < logicalValueBytes));
        Assert.Contains(frames, static frame => frame.Operation == PutOperation);
        Assert.All(
            frames.Where(static frame => frame.Operation == PutOperation),
            static frame => Assert.Equal((byte)1, frame.Compression));

        await using var reopened = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var visible = await reader.GetAsync("chunked-000"u8.ToArray());

        Assert.Equal(
            Enumerable.Repeat((byte)'x', 900),
            Assert.IsType<ReadOnlyMemory<byte>>(visible).ToArray());
    }

    [Fact]
    public async Task ShouldPreserveSequenceHoleGivenSpilledCommitFailsBeforeMarker()
    {
        using var directory = new TemporaryDirectory();
        var spilledBoundary = TransactionSpillHardeningTestHarness.GetRequiredFailpoint(
            "BeforeSpilledTransactionCommitMarker");
        var failpoints = new ThrowingTransactionCommitBoundaryFailpointHandler(spilledBoundary);
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalForTestingAsync(
            directory.Path,
            failpoints);
        await using (var spilled = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            TransactionSpillHardeningTestHarness.Fill(spilled, "failed", 12);
            await Assert.ThrowsAsync<PantsIOException>(() => spilled.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await using (var direct = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            direct.Put("survivor"u8.ToArray(), "visible"u8.ToArray());
            await direct.CommitAsync(PantsWriteOptions.Sync);
        }

        var frames = TransactionSpillHardeningTestHarness.ReadWalFrames(directory.Path);
        var sequences = frames.Select(static frame => frame.Sequence).ToArray();

        Assert.Equal(sequences.Length, sequences.Distinct().Count());
        Assert.True(sequences.SequenceEqual(sequences.Order()));
        Assert.DoesNotContain(
            frames,
            static frame => frame.Operation == TransactionCommitOperation);
        Assert.Equal(TransactionBatchOperation, frames[^1].Operation);
        Assert.Equal("visible", await TransactionSpillHardeningTestHarness.ReadTextAsync(
            database,
            "survivor"));
        Assert.Null(await TransactionSpillHardeningTestHarness.ReadTextAsync(database, "failed-000"));
    }

    [Fact]
    public async Task ShouldNotPublishSpilledWalGivenCommitMarkerAppendFails()
    {
        using var directory = new TemporaryDirectory();
        var spilledBoundary = TransactionSpillHardeningTestHarness.GetRequiredFailpoint(
            "BeforeSpilledTransactionCommitMarker");
        var failpoints = new ThrowingTransactionCommitBoundaryFailpointHandler(spilledBoundary);
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "spill-sequence-hole/")
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1_024))
            .WithMemtableLimits(24 * 1_024)
            .WithTransactionMemoryPool(1_024)
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(failpoints)))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            TransactionSpillHardeningTestHarness.Fill(transaction, "failed-cloud", 12);

            await Assert.ThrowsAsync<PantsIOException>(() =>
                transaction.CommitAsync(PantsWriteOptions.CloudAsync).AsTask());

            Assert.Null(await TransactionSpillHardeningTestHarness.ReadTextAsync(
                database,
                "failed-cloud-000"));
        }

        var cloudWalPath = Path.Combine(
            directory.Path,
            "cloud_store",
            "wal");
        using var catalog = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(
            cloudWalPath,
            "publication-catalog.v1.json")));

        Assert.Empty(catalog.RootElement.GetProperty("segments").EnumerateObject());
        Assert.Empty(Directory.GetFiles(cloudWalPath, "*.wal", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ShouldReachOnlyPathSpecificBoundaryWhenSpillAndDirectCommitRace()
    {
        using var directory = new TemporaryDirectory();
        var directBoundary = TransactionSpillHardeningTestHarness.GetRequiredFailpoint(
            "BeforeDirectTransactionCommitMarker");
        var spilledBoundary = TransactionSpillHardeningTestHarness.GetRequiredFailpoint(
            "BeforeSpilledTransactionCommitMarker");
        var handler = new TransactionCommitBoundaryFailpointHandler(directBoundary, spilledBoundary);
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalForTestingAsync(
            directory.Path,
            handler);
        await using var direct = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        await using var spilled = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        direct.Put("direct"u8.ToArray(), "value"u8.ToArray());
        TransactionSpillHardeningTestHarness.Fill(spilled, "isolation", 12);
        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var commitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var directCommit = CommitAfterSignalAsync(direct, start.Task, commitDeadline.Token);
        var spilledCommit = CommitAfterSignalAsync(spilled, start.Task, commitDeadline.Token);
        var commits = Task.WhenAll(directCommit, spilledCommit);
        var cleanupTimedOut = false;
        try
        {
            start.TrySetResult();
            await commits.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            start.TrySetResult();
            commitDeadline.Cancel();
            if (!commits.IsCompleted)
            {
                try
                {
                    await commits.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    cleanupTimedOut = true;
                }
                catch
                {
                    // Preserve the primary commit failure after observing both tasks.
                }
            }
        }

        Assert.False(
            cleanupTimedOut,
            "Concurrent transaction commits did not stop within the cleanup deadline.");

        Assert.Equal(1, handler.DirectHits);
        Assert.Equal(1, handler.SpilledHits);
    }

    static async Task CommitAfterSignalAsync(
        IPantsTransaction transaction,
        Task signal,
        CancellationToken cancellationToken)
    {
        await signal.WaitAsync(cancellationToken);
        await transaction.CommitAsync(PantsWriteOptions.Sync, cancellationToken);
    }
}
