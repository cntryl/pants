namespace Pants.Tests;

public sealed class CommitCoalescerTests
{
    static readonly ColumnFamilyIdentity Family = new(0, "default", 0);

    [Fact]
    public void ShouldAttemptOnlyConfiguredMultiCommitBatch()
    {
        var enabled = CreateCoalescer(enabled: true);
        var disabled = CreateCoalescer(enabled: false);
        var commits = new[] { CreateCommand(1), CreateCommand(2) };

        Assert.False(enabled.CanAttempt(commits[..1]));
        Assert.True(enabled.CanAttempt(commits));
        Assert.False(disabled.CanAttempt(commits));
    }

    [Fact]
    public void ShouldStageEligibleResidentCommitWithoutMutatingRuntimeAccounting()
    {
        var state = CreateState();
        var coalescer = CreateCoalescer(enabled: true, memtableSizeLimitBytes: 1_024);
        var stagedBytes = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        var command = CreateCommand(1);

        var staged = coalescer.TryStage(
            state,
            command,
            groupDurability: null,
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
            enabled: true,
            memtableSizeLimitBytes: 1_024,
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
            PantsFailpoint.BeforeCoalescedWalDurabilityBoundary);

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
        var coalescer = CreateCoalescer(enabled: true);
        var stagedBytes = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);

        Assert.True(coalescer.TryStage(
            state,
            CreateCommand(1, PantsDurability.Buffered),
            groupDurability: null,
            stagedBytes));
        Assert.True(coalescer.TryStage(
            state,
            CreateCommand(2, PantsDurability.Buffered),
            PantsDurability.Buffered,
            stagedBytes));
        Assert.False(coalescer.TryStage(
            state,
            CreateCommand(3, PantsDurability.Sync),
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
            enabled: true,
            memtableSizeLimitBytes: 1_024,
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
            PantsFailpoint.BeforeCoalescedWalDurabilityBoundary);

        Assert.Equal(PantsDurability.Buffered, appendedDurability);
        Assert.Equal(0, telemetry.DurabilityWaitersFannedOut);
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

    static PantsRuntimeState CreateState() =>
        new(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());

    static CommitRuntimeCommand CreateCommand(
        long transactionId,
        PantsDurability durability = PantsDurability.Sync)
    {
        var state = CreateState();
        var operation = new TransactionIntentOperation(
            ordinal: 0,
            CommitOperationKind.Put,
            Family,
            "key"u8.ToArray(),
            endExclusive: null,
            "v"u8.ToArray(),
            timeToLive: null,
            expiryUtc: null,
            insertOnly: false);
        var source = new TransactionOperationSource(
            spillStore: null,
            [operation],
            count: 1,
            DateTimeOffset.UnixEpoch);
        var payload = new CommitPayload(
            transactionId,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateSnapshot(),
            source,
            []);
        var writeOptions = durability switch
        {
            PantsDurability.Sync => PantsWriteOptions.Sync,
            PantsDurability.Buffered => PantsWriteOptions.Buffered,
            _ => throw new ArgumentOutOfRangeException(nameof(durability))
        };
        return new CommitRuntimeCommand(
            writeOptions,
            payload,
            static _ => ValueTask.FromResult(false));
    }
}
