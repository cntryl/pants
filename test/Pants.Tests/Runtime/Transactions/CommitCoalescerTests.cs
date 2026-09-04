namespace Cntryl.Pants.Tests.Runtime.Transactions;

public sealed class CommitCoalescerTests
{
    static readonly ColumnFamilyIdentity Family = new(0, "default", 0);

    [Fact]
    public void ShouldAttemptOnlyConfiguredMultiCommitBatch()
    {
        var enabled = CreateCoalescer(true);
        var disabled = CreateCoalescer(false);
        var commits = new[] { CreateCommand(1), CreateCommand(2) };

        Assert.False(enabled.CanAttempt(commits[..1]));
        Assert.True(enabled.CanAttempt(commits));
        Assert.False(disabled.CanAttempt(commits));
    }

    [Fact]
    public void ShouldStageEligibleResidentCommitWithoutMutatingRuntimeAccounting()
    {
        var state = CreateState();
        var coalescer = CreateCoalescer(true);
        var stagedBytes = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        var command = CreateCommand(1);

        var staged = coalescer.TryStage(
            state,
            command,
            null,
            stagedBytes);

        Assert.True(staged);
        Assert.Equal(68, stagedBytes[Family]);
        Assert.Equal(0, state.ActiveMemtableBytes[Family]);
    }

    [Fact]
    public async Task ShouldAppendPreparedCommitsThroughOnePhysicalGroupCall()
    {
        var state = CreateState();
        var telemetry = new RuntimeTelemetry();
        var appendCalls = 0;
        IReadOnlyList<WalCommitGroupEntry>? appended = null;
        var coalescer = new CommitCoalescer(
            true,
            1_024,
            telemetry,
            (commits, _, _, _) =>
            {
                appendCalls++;
                appended = commits;
                return ValueTask.FromResult(new WalCommitGroupResult(commits.Count));
            },
            static (_, _, _) => { });
        var prepared = CommitCoalescer.CreatePreparedCommits(
            state,
            [CreateCommand(1), CreateCommand(2)]);

        await coalescer.AppendAsync(
            state,
            prepared,
            PantsDurability.Sync,
            Failpoint.BeforeCoalescedWalDurabilityBoundary);

        Assert.Equal(1, appendCalls);
        Assert.Equal([3L, 6L], Assert.IsAssignableFrom<IReadOnlyList<WalCommitGroupEntry>>(appended)
            .Select(static commit => commit.ExpectedSequence));
        Assert.Equal(0, telemetry.WalAppendCount);
        Assert.Equal(0, telemetry.WalFlushCount);
        Assert.Equal(0, telemetry.WalFsyncCount);
        Assert.Equal(2, telemetry.DurabilityWaitersFannedOut);
        Assert.Equal(0, telemetry.WalLastSyncedSequence);
    }

    [Fact]
    public void ShouldStageOnlyHomogeneousBufferedGroup()
    {
        var state = CreateState();
        var coalescer = CreateCoalescer(true);
        var stagedBytes = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);

        Assert.True(coalescer.TryStage(
            state,
            CreateCommand(1, PantsDurability.Buffered),
            null,
            stagedBytes));
        Assert.True(coalescer.TryStage(
            state,
            CreateCommand(2, PantsDurability.Buffered),
            PantsDurability.Buffered,
            stagedBytes));
        Assert.False(coalescer.TryStage(
            state,
            CreateCommand(3),
            PantsDurability.Buffered,
            stagedBytes));
        Assert.Equal(136, stagedBytes[Family]);
    }

    [Fact]
    public async Task ShouldNotRecordDurabilityFanoutGivenBufferedGroup()
    {
        var state = CreateState();
        var telemetry = new RuntimeTelemetry();
        PantsDurability? appendedDurability = null;
        var coalescer = new CommitCoalescer(
            true,
            1_024,
            telemetry,
            (commits, _, durability, _) =>
            {
                appendedDurability = durability;
                return ValueTask.FromResult(new WalCommitGroupResult(commits.Count));
            },
            static (_, _, _) => { });
        var prepared = CommitCoalescer.CreatePreparedCommits(
            state,
            [
                CreateCommand(1, PantsDurability.Buffered),
                CreateCommand(2, PantsDurability.Buffered)
            ]);

        await coalescer.AppendAsync(
            state,
            prepared,
            PantsDurability.Buffered,
            Failpoint.BeforeCoalescedWalDurabilityBoundary);

        Assert.Equal(PantsDurability.Buffered, appendedDurability);
        Assert.Equal(0, telemetry.DurabilityWaitersFannedOut);
    }

    [Theory]
    [InlineData(PantsDurability.BestEffort, PantsDurability.BestEffort)]
    [InlineData(PantsDurability.CloudAsync, PantsDurability.Buffered)]
    public async Task ShouldAppendExtendedDurabilityGroupAtLocalBoundary(
        PantsDurability requested,
        PantsDurability expectedWalDurability)
    {
        var state = CreateState();
        PantsDurability? appendedDurability = null;
        var coalescer = new CommitCoalescer(
            true,
            1_024,
            new RuntimeTelemetry(),
            (commits, _, durability, _) =>
            {
                appendedDurability = durability;
                return ValueTask.FromResult(new WalCommitGroupResult(commits.Count));
            },
            static (_, _, _) => { });
        var commands = new[] { CreateCommand(1, requested), CreateCommand(2, requested) };
        var stagedBytes = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);

        Assert.All(commands, command => Assert.True(coalescer.TryStage(
            state,
            command,
            requested,
            stagedBytes)));
        await coalescer.AppendAsync(
            state,
            CommitCoalescer.CreatePreparedCommits(state, commands),
            requested,
            Failpoint.BeforeCoalescedWalDurabilityBoundary);

        Assert.Equal(expectedWalDurability, appendedDurability);
    }

    static CommitCoalescer CreateCoalescer(
        bool enabled,
        long memtableSizeLimitBytes = 1_024) =>
        new(
            enabled,
            memtableSizeLimitBytes,
            new RuntimeTelemetry(),
            static (_, _, _, _) => throw new InvalidOperationException("Append was not expected."),
            static (_, _, _) => { });

    static RuntimeState CreateState() =>
        new(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());

    static CommitRuntimeCommand CreateCommand(
        long transactionId,
        PantsDurability durability = PantsDurability.Sync)
    {
        var state = CreateState();
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            Family,
            "key"u8.ToArray(),
            null,
            "v"u8.ToArray(),
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        var payload = new CommitPayload(
            transactionId,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
        var writeOptions = durability switch
        {
            PantsDurability.Sync => PantsWriteOptions.Sync,
            PantsDurability.Buffered => PantsWriteOptions.Buffered,
            PantsDurability.BestEffort => PantsWriteOptions.BestEffort,
            PantsDurability.CloudAsync => PantsWriteOptions.CloudAsync,
            _ => throw new ArgumentOutOfRangeException(nameof(durability))
        };
        return new CommitRuntimeCommand(
            writeOptions,
            payload,
            static _ => ValueTask.FromResult(false),
            new RuntimeResponseRegistry(new RuntimeTelemetry(), TimeProvider.System),
            transactionId,
            "Commit");
    }
}
