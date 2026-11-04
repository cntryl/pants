using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.Manifest;
using Cntryl.Pants.Storage.Internal.Recovery;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class Format9MetadataTests
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData("empty-local")]
    [InlineData("populated-local")]
    public async Task ShouldOpenAndReopenReferenceDatabaseGivenFormat9Fixture(string fixture)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "LsmSpec", "7748fd1", "conformance", "fixtures", "format9", fixture);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(directory.Path, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
            await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            var value = await transaction.GetAsync("fixture/alpha"u8.ToArray());
            if (fixture == "populated-local")
            {
                Assert.Equal("value-alpha"u8.ToArray(), Assert.IsType<ReadOnlyMemory<byte>>(value).ToArray());
            }
            else
            {
                Assert.Null(value);
            }
        }
    }

    [Theory]
    [InlineData("empty-local", 0)]
    [InlineData("populated-local", 1)]
    public void ShouldDecodeReferenceManifestGivenFormat9Fixture(string fixture, int expectedFiles)
    {
        using var document = MetadataFrameCodec.Decode(ReadFixture(fixture, "manifest.snapshot.json"), 1);
        var manifest = ManifestCodec.Decode(document.RootElement, Options);
        Assert.Equal(expectedFiles, manifest.Files.Count);
        using var encoded = MetadataFrameCodec.Decode(ManifestCodec.Encode(manifest, Options), 1);
        var reopened = ManifestCodec.Decode(encoded.RootElement, Options);
        Assert.Equal(manifest.FlushedThrough, reopened.FlushedThrough);
        Assert.Equal(manifest.EditCheckpointId, reopened.EditCheckpointId);
        Assert.Equal(manifest.Files.Select(static file => file.Name), reopened.Files.Select(static file => file.Name));
    }

    [Fact]
    public void ShouldDecodeReferenceIntentGivenFormat9Fixture()
    {
        var state = IntentLogCodec.Read(ReadFixture("populated-local", "intent_log.json"));
        Assert.Equal(2UL, state.Sequence);
        Assert.Empty(state.Entries);
    }

    [Fact]
    public void ShouldRetainVerifiedIntentPrefixGivenTornFinalAppend()
    {
        var empty = IntentLogCodec.Read([]);
        var first = IntentLogCodec.Read(IntentLogCodec.CreateEdit(empty, [IntentEntry.Create("SeqnoAllocated", new { seqno = 1UL, cf_id = 0U }, Options)]));
        var clear = IntentLogCodec.CreateEdit(first, []);
        var torn = first.Bytes.Concat(clear.Take(clear.Length - 1)).ToArray();
        var recovered = IntentLogCodec.Read(torn);
        Assert.Single(recovered.Entries);
        Assert.Equal(first.FileLength, recovered.ValidLength);
        var complete = IntentLogCodec.Read(first.Bytes.Concat(clear).ToArray());
        Assert.Empty(complete.Entries);
        Assert.Equal(2UL, complete.Sequence);
    }

    [Fact]
    public void ShouldRejectBrokenIntentChainGivenChecksummedCompleteEdit()
    {
        var first = IntentLogCodec.Read(ReadFixture("populated-local", "intent_log.json"));
        var invalid = MetadataFrameCodec.Encode(3, JsonSerializer.SerializeToElement(new
        {
            sequence = 2UL,
            previous_checksum = first.Checksum ^ 1,
            keep = 0,
            append = Array.Empty<object>()
        }));
        Assert.Throws<PantsCorruptionException>(() => IntentLogCodec.Read(first.Bytes.Concat(invalid).ToArray()));
    }

    [Fact]
    public void ShouldRejectWrongKindAndTrailingBytesGivenChecksummedFrame()
    {
        var frame = MetadataFrameCodec.Encode(1, "{}"u8);
        Assert.Throws<PantsCorruptionException>(() => MetadataFrameCodec.Decode(frame, 2));
        Assert.Throws<PantsCorruptionException>(() => MetadataFrameCodec.Decode(frame.Concat(new byte[] { 0 }).ToArray(), 1));
    }

    [Theory]
    [InlineData(new byte[] { 0xc1 })]
    [InlineData(new byte[] { 0xdd, 0xff, 0xff, 0xff, 0xff })]
    [InlineData(new byte[] { 0xa2, 0x61 })]
    [InlineData(new byte[] { 0x80, 0xc0 })]
    public void ShouldRejectMalformedMessagePackGivenValidFrameChecksum(byte[] payload)
    {
        var frame = new byte[17 + payload.Length];
        "MDGEMD09"u8.CopyTo(frame);
        frame[8] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), (uint)payload.Length);
        payload.CopyTo(frame, 13);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(frame.Length - 4), DiskFormat.Crc32C(frame.AsSpan(0, frame.Length - 4)));
        Assert.Throws<PantsCorruptionException>(() => MetadataFrameCodec.Decode(frame, 1));
    }

    [Fact]
    public void ShouldRetainRetiredIdentityGivenCheckpointRoundTrip()
    {
        var manifest = ManifestState.CreateInitial();
        var file = new FileMeta { Name = "source.sst", ContentCrc32C = 42, SmallestKey = [1], LargestKey = [2] };
        ManifestTransitions.Add(manifest, file);
        ManifestTransitions.Remove(manifest, file.Name);
        using var document = MetadataFrameCodec.Decode(ManifestCodec.Encode(manifest, Options), 1);
        var recovered = ManifestCodec.Decode(document.RootElement, Options);
        Assert.Empty(recovered.Files);
        Assert.True(ManifestTransitions.SameIdentity(file, Assert.Single(recovered.RetiredSsts).Value));
        Assert.Throws<PantsCorruptionException>(() => ManifestTransitions.Add(recovered, file));
        ManifestTransitions.CompleteDelete(recovered, file);
        Assert.Empty(recovered.RetiredSsts);
    }

    [Fact]
    public void ShouldMoveImmutableIdentitiesGivenNonoverlappingCoverage()
    {
        var manifest = ManifestState.CreateInitial();
        var file = new FileMeta
        {
            Name = "source.sst",
            Level = 1,
            ContentCrc32C = 42,
            SmallestKey = [1],
            LargestKey = [2],
            KeyBoundsComplete = true
        };
        ManifestTransitions.Add(manifest, file.Clone());
        var edit = JsonSerializer.SerializeToElement(new { captured = new[] { file }, target_level = 2 }, Options);
        ManifestTransitions.Move(manifest, edit, Options);
        ManifestTransitions.Move(manifest, edit, Options);
        Assert.Equal(2U, Assert.Single(manifest.Files).Level);
        Assert.Empty(manifest.RetiredSsts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectPhaseOnlyPublicationGivenAuthoritativeManifestDoesNotContainOutput(bool compaction)
    {
        using var directory = Format9TestMetadata.CopyFixture("populated-local");
        var manifestPath = Path.Combine(directory.Path, "manifest.snapshot.json");
        var manifest = Format9TestMetadata.ReadNode(manifestPath);
        var source = manifest["files"]![0]!.DeepClone();
        var sourceName = source["name"]!.GetValue<string>();
        var originalSst = File.ReadAllBytes(Path.Combine(directory.Path, "sst", sourceName));
        var runtime = source.DeepClone().AsObject();
        runtime.Remove("sst_seq");
        runtime.Remove("sublevel");
        IntentEntry intent;
        if (compaction)
        {
            runtime["name"] = "unpublished.sst";
            File.Copy(Path.Combine(directory.Path, "sst", sourceName), Path.Combine(directory.Path, "sst", "unpublished.sst"));
            intent = IntentEntry.Create("CompactionPublish", new
            {
                phase = "ManifestPublished",
                cf_id = 0U,
                removed = new[] { sourceName },
                added = new[] { runtime }
            }, Options);
        }
        else
        {
            manifest["files"] = new System.Text.Json.Nodes.JsonArray();
            await Format9TestMetadata.WriteAsync(manifestPath, manifest.ToJsonString());
            intent = IntentEntry.Create("FlushPublish", new
            {
                phase = "ManifestPublished",
                cf_id = 0U,
                sequence = 1UL,
                file_meta = runtime
            }, Options);
        }

        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "intent_log.json"), IntentLogCodec.CreateEdit(IntentLogCodec.Read([]), [intent]));
        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());
        Assert.Equal(originalSst, File.ReadAllBytes(Path.Combine(directory.Path, "sst", sourceName)));
        using var authoritative = Format9TestMetadata.Read(manifestPath);
        Assert.Equal(compaction ? 1 : 0, authoritative.RootElement.GetProperty("files").GetArrayLength());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldRequireUnsignedIntegerAllocatorFamilyKeysGivenMessagePackMap(bool stringKey)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(0x81);
        payload.WriteByte(0xad);
        payload.Write("next_sst_seqs"u8);
        payload.WriteByte(0x81);
        if (stringKey)
        {
            payload.WriteByte(0xa1);
            payload.WriteByte((byte)'0');
        }
        else
        {
            payload.WriteByte(0);
        }

        payload.WriteByte(1);
        var frame = EncodeFrame(1, payload.ToArray());
        if (stringKey)
        {
            Assert.Throws<PantsCorruptionException>(() => MetadataFrameCodec.Decode(frame, 1));
        }
        else
        {
            using var decoded = MetadataFrameCodec.Decode(frame, 1);
            Assert.Equal(1UL, decoded.RootElement.GetProperty("next_sst_seqs").GetProperty("0").GetUInt64());
        }
    }

    [Theory]
    [InlineData("name")]
    [InlineData("level")]
    [InlineData("size_bytes")]
    public void ShouldRejectManifestGivenFileIdentityIsMissingRequiredField(string field)
    {
        var manifest = System.Text.Json.Nodes.JsonNode.Parse("""
            {"files":[{"name":"source.sst","level":0,"size_bytes":1}],"flushed_through":[{"cf_id":0,"through_sequence":0}]}
            """)!;
        manifest["files"]![0]!.AsObject().Remove(field);
        var frame = MetadataFrameCodec.Encode(1, System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        Assert.Throws<JsonException>(() => ManifestCodec.Decode(frame));
    }

    static byte[] EncodeFrame(byte kind, byte[] payload)
    {
        var frame = new byte[17 + payload.Length];
        "MDGEMD09"u8.CopyTo(frame);
        frame[8] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), (uint)payload.Length);
        payload.CopyTo(frame, 13);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(frame.Length - 4), DiskFormat.Crc32C(frame.AsSpan(0, frame.Length - 4)));
        return frame;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("live")]
    [InlineData("foreign")]
    [InlineData("uncaptured")]
    [InlineData("duplicate")]
    public void ShouldRejectInvalidReclamationWithoutApplyingEarlierBatchEdits(string scenario)
    {
        var manifest = ManifestState.CreateInitial();
        manifest.NextSstSeqs[0] = 1;
        manifest.Files.Add(MetadataFile("default.sst"));
        var names = new[] { "default.sst" };
        if (scenario != "missing")
        {
            manifest.ColumnFamilies.Add(new ColumnFamilyMeta
            {
                Id = 1,
                Name = "other",
                CreatedAt = 1,
                DeletedAt = scenario == "live" ? null : 2,
                DropSequence = scenario == "live" ? null : 2,
                DroppedSstNames = scenario == "uncaptured" ? [] : ["owned.sst"]
            });
            manifest.FlushedThrough[1] = scenario == "live" ? 0 : ulong.MaxValue;
            manifest.Files.Add(MetadataFile("owned.sst", 1));
            names = scenario == "foreign" ? ["default.sst"] : scenario == "duplicate" ? ["owned.sst", "owned.sst"] : ["owned.sst"];
        }

        var edit = new ManifestEdit("Batch", new List<ManifestEdit>
        {
            new("BumpNextSstSeq", new { cf_id = 0U, next_seq = 10UL }),
            new("ReclaimColumnFamily", new { id = scenario == "missing" ? 999U : 1U, names })
        });
        var before = manifest.Files.Select(static file => file.Name).ToArray();
        Assert.ThrowsAny<PantsException>(() => LocalDiskStore.ReplayManifestMetadata(EncodeJournal(edit, 1), manifest));
        Assert.Equal(before, manifest.Files.Select(static file => file.Name));
        Assert.Equal(1UL, manifest.NextSstSeqs[0]);
        Assert.Empty(manifest.RetiredSsts);
    }

    [Fact]
    public void ShouldAcceptRepeatedReclamationAfterOwnedSstDeletionWasAcknowledged()
    {
        var manifest = ManifestState.CreateInitial();
        manifest.ColumnFamilies.Add(new ColumnFamilyMeta { Id = 1, Name = "dropped", CreatedAt = 1, DeletedAt = 2, DropSequence = 2, DroppedSstNames = ["owned.sst"] });
        manifest.FlushedThrough[1] = ulong.MaxValue;
        var file = MetadataFile("owned.sst", 1);
        manifest.Files.Add(file);
        var reclaim = new ManifestEdit("ReclaimColumnFamily", new { id = 1U, names = new[] { file.Name } });
        LocalDiskStore.ReplayManifestMetadata(EncodeJournal(reclaim, 1), manifest);
        LocalDiskStore.ReplayManifestMetadata(EncodeJournal(new ManifestEdit("CompleteSstDelete", new { file }), 2), manifest);
        LocalDiskStore.ReplayManifestMetadata(EncodeJournal(reclaim, 3), manifest);
        Assert.Empty(manifest.Files);
        Assert.Empty(manifest.RetiredSsts);
        Assert.True(Assert.Single(manifest.ColumnFamilies).Reclaimed);
        Assert.Equal(ulong.MaxValue, manifest.FlushedThrough[1]);
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("later-high")]
    [InlineData("duplicate")]
    [InlineData("removed")]
    [InlineData("remove-ack-add")]
    public void ShouldRejectInvalidNestedBatchWithoutAdvancingFlushAuthority(string scenario)
    {
        var manifest = ManifestState.CreateInitial();
        var existing = MetadataFile("existing.sst");
        manifest.Files.Add(existing);
        var output = scenario == "existing" ? existing : MetadataFile("new.sst");
        var children = new List<ManifestEdit>
        {
            new("AddSst", output),
            new("AdvanceFlushFrontier", new { cf_id = 0U, freeze_sequence = 10UL })
        };
        if (scenario == "later-high")
        {
            children.Add(new ManifestEdit("AddSst", MetadataFile("too-new.sst", largestSequence: 11)));
        }
        else if (scenario == "duplicate")
        {
            children.Add(new ManifestEdit("AdvanceFlushFrontier", new { cf_id = 0U, freeze_sequence = 10UL }));
        }
        else if (scenario is "removed" or "remove-ack-add")
        {
            children.Add(new ManifestEdit("RemoveSst", new { name = output.Name }));
            if (scenario == "remove-ack-add")
            {
                children.Add(new ManifestEdit("CompleteSstDelete", new { file = output }));
                children.Add(new ManifestEdit("AddSst", output));
            }
        }

        var nested = new ManifestEdit("Batch", new List<ManifestEdit> { new("Batch", children) });
        Assert.ThrowsAny<PantsException>(() => LocalDiskStore.ReplayManifestMetadata(EncodeJournal(nested, 1), manifest));
        Assert.Equal(0UL, manifest.FlushedThrough[0]);
        Assert.Equal(existing.Name, Assert.Single(manifest.Files).Name);
        Assert.Empty(manifest.RetiredSsts);
    }

    [Fact]
    public void ShouldAdvanceFlushAuthorityGivenOuterOutputAndNestedFrontier()
    {
        var manifest = ManifestState.CreateInitial();
        var output = MetadataFile("new.sst");
        var batch = new ManifestEdit("Batch", new List<ManifestEdit>
        {
            new("AddSst", output),
            new("Batch", new List<ManifestEdit> { new("AdvanceFlushFrontier", new { cf_id = 0U, freeze_sequence = 10UL }) })
        });
        LocalDiskStore.ReplayManifestMetadata(EncodeJournal(batch, 1), manifest);
        Assert.Equal(10UL, manifest.FlushedThrough[0]);
        Assert.Equal(output.Name, Assert.Single(manifest.Files).Name);
    }

    static FileMeta MetadataFile(string name, uint familyId = 0, ulong largestSequence = 1) => new()
    {
        Name = name,
        Level = 0,
        SizeBytes = 1,
        ColumnFamilyId = familyId,
        SmallestSequence = 1,
        LargestSequence = largestSequence
    };

    static byte[] EncodeJournal(ManifestEdit edit, ulong editId)
    {
        using var stream = new MemoryStream();
        var type = edit.Variant switch { "Batch" => (byte)8, "ReclaimColumnFamily" => (byte)11, "CompleteSstDelete" => (byte)13, _ => throw new InvalidOperationException() };
        Span<byte> checksum = stackalloc byte[4];
        foreach (var item in new[]
        {
            (Type: type, Payload: MetadataFrameCodec.Encode(2, JsonSerializer.SerializeToElement(new { edit_id = editId, edit }, Options))),
            (Type: (byte)9, Payload: MetadataFrameCodec.Encode(2, JsonSerializer.SerializeToElement(new { last_persisted_sequence = editId, ts_millis = 1UL })))
        })
        {
            stream.WriteByte(item.Type);
            DiskFormat.WriteUInt32(stream, (uint)item.Payload.Length);
            stream.Write(item.Payload);
            BinaryPrimitives.WriteUInt32BigEndian(checksum, DiskFormat.Crc32C(item.Payload));
            stream.Write(checksum);
        }

        return stream.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectSstAdditionGivenFamilyIsMissingOrDropped(bool dropped)
    {
        var manifest = ManifestState.CreateInitial();
        if (dropped)
        {
            manifest.ColumnFamilies.Add(new ColumnFamilyMeta { Id = 1, Name = "dropped", CreatedAt = 1, DeletedAt = 2, DropSequence = 2 });
            manifest.FlushedThrough[1] = ulong.MaxValue;
        }

        var batch = new ManifestEdit("Batch", new List<ManifestEdit> { new("AddSst", MetadataFile("new.sst", 1)) });
        Assert.ThrowsAny<PantsException>(() => LocalDiskStore.ReplayManifestMetadata(EncodeJournal(batch, 1), manifest));
        Assert.Empty(manifest.Files);
        Assert.Empty(manifest.RetiredSsts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("default")]
    [InlineData("foreign")]
    [InlineData("conflict")]
    public void ShouldRejectInvalidFamilyDropWithoutChangingLiveSstAuthority(string scenario)
    {
        var manifest = ManifestState.CreateInitial();
        manifest.Files.Add(MetadataFile("default.sst"));
        if (scenario is "foreign" or "conflict")
        {
            manifest.ColumnFamilies.Add(new ColumnFamilyMeta
            {
                Id = 1,
                Name = "other",
                CreatedAt = 1,
                DeletedAt = scenario == "conflict" ? 2UL : null,
                DropSequence = scenario == "conflict" ? 2UL : null
            });
            manifest.FlushedThrough[1] = scenario == "conflict" ? ulong.MaxValue : 0;
        }

        var id = scenario == "default" ? 0U : 1U;
        var names = scenario == "foreign" ? new[] { "default.sst" } : [];
        var batch = new ManifestEdit("Batch", new List<ManifestEdit>
        {
            new("DropColumnFamilyAt", new { id, drop_sequence = 3UL, dropped_sst_names = names })
        });
        Assert.ThrowsAny<PantsException>(() => LocalDiskStore.ReplayManifestMetadata(EncodeJournal(batch, 1), manifest));
        Assert.Equal("default.sst", Assert.Single(manifest.Files).Name);
        Assert.Equal(0UL, manifest.FlushedThrough[0]);
    }

    [Fact]
    public async Task ShouldReopenDroppedFamilyAfterReplayingItsOriginalDropAgain()
    {
        using var directory = Format9TestMetadata.CopyFixture("empty-local");
        var manifest = ManifestState.CreateInitial();
        manifest.ColumnFamilies.Add(new ColumnFamilyMeta { Id = 1, Name = "dropped", CreatedAt = 1 });
        manifest.FlushedThrough[1] = 0;
        var drop = new ManifestEdit("Batch", new List<ManifestEdit>
        {
            new("DropColumnFamilyAt", new { id = 1U, drop_sequence = 2UL, dropped_sst_names = Array.Empty<string>() })
        });
        LocalDiskStore.ReplayManifestMetadata(EncodeJournal(drop, 1), manifest);
        LocalDiskStore.ReplayManifestMetadata(EncodeJournal(drop, 2), manifest);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.snapshot.json"), ManifestCodec.Encode(manifest, Options));
        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.Null(await reopened.ColumnFamilies.GetAsync("dropped"));
        Assert.Equal(ulong.MaxValue, CloudManifestReader.ReadManifest(directory.Path)!.FlushedThrough[1]);
    }

    static byte[] ReadFixture(string fixture, string name) => File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Compatibility", "LsmSpec", "7748fd1", "conformance", "fixtures", "format9", fixture, name));
}
