using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudSstRetirementTests
{
    const string Name = "000000_00_00000000000000000001.sst";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAcknowledgeExactRetiredIdentityAfterDeletionOrConfirmedAbsence(bool alreadyAbsent)
    {
        var store = new ScriptedCloudObjectStore();
        var bytes = SstCodec.Encode([new SstEntry("key"u8.ToArray(), "value"u8.ToArray(), 1, null, false)], [], PantsPerformanceGoal.Latency);
        var retired = CreateIdentity(bytes);
        if (!alreadyAbsent)
        {
            store.Write(PantsCloudObjectLayout.SstPrefix + Name, bytes);
        }

        var acknowledgements = 0;
        var budget = new ResourceBudget(256 * 1024);
        var collector = CreateCollector(store, retired, budget, () => true, file =>
        {
            Assert.Equal(retired.ContentCrc32C, file.ContentCrc32C);
            acknowledgements++;
        });

        Assert.True(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(1, acknowledgements);
        Assert.False(store.Contains(PantsCloudObjectLayout.SstPrefix + Name));
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRetainDeletionObligationWhenRemoteBytesDoNotMatchRetiredChecksum()
    {
        var store = new ScriptedCloudObjectStore();
        byte[] expected = [1, 2, 3, 4];
        store.Write(PantsCloudObjectLayout.SstPrefix + Name, [1, 2, 3, 5]);
        var acknowledgements = 0;
        var budget = new ResourceBudget(256 * 1024);
        var collector = CreateCollector(store, CreateIdentity(expected), budget, () => true, _ => acknowledgements++);

        Assert.False(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(0, acknowledgements);
        Assert.True(store.Contains(PantsCloudObjectLayout.SstPrefix + Name));
        Assert.DoesNotContain(store.Requests, static request => request.Operation == StoreOperation.Delete);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRetainRetiredSstWhenReadDependencyPreventsDeletionAfterByteVerification()
    {
        var store = new ScriptedCloudObjectStore();
        byte[] bytes = [1, 2, 3, 4];
        store.Write(PantsCloudObjectLayout.SstPrefix + Name, bytes);
        var eligible = true;
        store.BeforeRangeRead = (_, _) =>
        {
            eligible = false;
            return ValueTask.CompletedTask;
        };
        var acknowledgements = 0;
        var budget = new ResourceBudget(256 * 1024);
        var collector = CreateCollector(store, CreateIdentity(bytes), budget, () => eligible, _ => acknowledgements++);

        Assert.False(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(0, acknowledgements);
        Assert.True(store.Contains(PantsCloudObjectLayout.SstPrefix + Name));
        Assert.DoesNotContain(store.Requests, static request => request.Operation == StoreOperation.Delete);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldAcknowledgeRetiredSstProofEntriesAcrossFreshBoundedSweeps()
    {
        var store = new ScriptedCloudObjectStore();
        var identities = Enumerable.Range(1, 10)
            .Select(static sequence => new FileMeta
            {
                Name = $"000000_00_{sequence:D20}.sst",
                SizeBytes = checked((ulong)(100 + sequence)),
                ContentCrc32C = checked((uint)(0x1000 + sequence)),
                ColumnFamilyId = 0,
                SstSequence = checked((ulong)sequence)
            })
            .ToDictionary(static identity => identity.Name, StringComparer.Ordinal);
        var pending = new Dictionary<string, FileMeta>(identities, StringComparer.Ordinal);
        var acknowledgements = new List<(int ProofId, FileMeta Identity)>();
        var reservations = new List<TrackingProofReservation>();
        var proofCaptureCount = 0;
        var previousProofDisposedBeforeNextCapture = true;
        TrackingProofReservation? previousReservation = null;
        var collector = new CloudSstGarbageCollector(
            store,
            _ =>
            {
                if (previousReservation is not null && !previousReservation.IsDisposed)
                {
                    previousProofDisposedBeforeNextCapture = false;
                }

                var proofId = ++proofCaptureCount;
                var proofIdentities = pending.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);
                var reservation = new TrackingProofReservation();
                reservations.Add(reservation);
                previousReservation = reservation;
                return ValueTask.FromResult(new CloudSstRetentionProof(
                    new HashSet<string>(StringComparer.Ordinal),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    new Dictionary<uint, ulong>(),
                    [],
                    reservation,
                    proofIdentities,
                    file =>
                    {
                        acknowledgements.Add((proofId, file));
                        pending.Remove(file.Name);
                    },
                    file => proofIdentities.TryGetValue(file.Name, out var exact) && ReferenceEquals(exact, file)));
            },
            static () => new HashSet<string>(StringComparer.Ordinal),
            static () => { },
            static _ => ValueTask.CompletedTask,
            NullPantsFailpointHandler.Instance);

        Assert.All(identities.Keys, name => Assert.False(store.Contains(PantsCloudObjectLayout.SstPrefix + name)));

        Assert.True(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(1, proofCaptureCount);
        Assert.Equal(8, acknowledgements.Count);
        Assert.All(acknowledgements, acknowledgement => Assert.Equal(1, acknowledgement.ProofId));
        Assert.Equal(2, pending.Count);
        Assert.True(reservations[0].IsDisposed);

        Assert.True(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(2, proofCaptureCount);
        Assert.True(previousProofDisposedBeforeNextCapture);
        Assert.Equal(10, acknowledgements.Count);
        Assert.All(acknowledgements.Skip(8), acknowledgement => Assert.Equal(2, acknowledgement.ProofId));
        Assert.Empty(pending);
        Assert.All(reservations, static reservation => Assert.True(reservation.IsDisposed));
        Assert.DoesNotContain(store.Requests, static request => request.Operation == StoreOperation.Delete);

        Assert.Equal(identities.Count, acknowledgements.Select(static acknowledgement => acknowledgement.Identity.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var (name, expected) in identities)
        {
            var acknowledged = Assert.Single(acknowledgements, acknowledgement => acknowledgement.Identity.Name == name);
            Assert.Equal(expected.SizeBytes, acknowledged.Identity.SizeBytes);
            Assert.Equal(expected.ContentCrc32C, acknowledged.Identity.ContentCrc32C);
            Assert.Equal(expected.ColumnFamilyId, acknowledged.Identity.ColumnFamilyId);
            Assert.Equal(expected.SstSequence, acknowledged.Identity.SstSequence);
        }
    }

    static FileMeta CreateIdentity(byte[] bytes) => new()
    {
        Name = Name,
        SizeBytes = checked((ulong)bytes.Length),
        ContentCrc32C = DiskFormat.Crc32C(bytes),
        ColumnFamilyId = 0,
        SstSequence = 1
    };

    static CloudSstGarbageCollector CreateCollector(
        ICloudObjectStore store, FileMeta retired, ResourceBudget budget, Func<bool> eligible, Action<FileMeta> acknowledge) => new(
        store,
        _ => ValueTask.FromResult(new CloudSstRetentionProof(
            new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, string>(), new Dictionary<uint, ulong>(), [],
            RetiredSsts: new Dictionary<string, FileMeta>(StringComparer.Ordinal) { [Name] = retired },
            AcknowledgeRetiredSst: acknowledge, ValidateRetiredSst: _ => eligible(), ValidationBudget: budget)),
        static () => new HashSet<string>(StringComparer.Ordinal), static () => { }, static _ => ValueTask.CompletedTask,
        NullPantsFailpointHandler.Instance);

    sealed class TrackingProofReservation : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
