using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Manifest;

static class ManifestTransitions
{
    public static bool SameIdentity(FileMeta left, FileMeta right) =>
        left.Name == right.Name && left.Level == right.Level && left.SizeBytes == right.SizeBytes &&
        left.ContentCrc32C == right.ContentCrc32C && left.ColumnFamilyId == right.ColumnFamilyId &&
        left.SstSequence == right.SstSequence && left.Sublevel == right.Sublevel &&
        left.SmallestSequence == right.SmallestSequence && left.LargestSequence == right.LargestSequence &&
        left.KeyBoundsComplete == right.KeyBoundsComplete && left.EarliestExpirationMillis == right.EarliestExpirationMillis &&
        SameBytes(left.SmallestKey, right.SmallestKey) && SameBytes(left.LargestKey, right.LargestKey);

    public static void Add(ManifestState manifest, FileMeta metadata)
    {
        if (!SstFileName.IsSafe(metadata.Name) || manifest.RetiredSsts.ContainsKey(metadata.Name))
        {
            throw new PantsCorruptionException("SST identity is unsafe or awaiting retirement.");
        }

        if (!manifest.FlushedThrough.TryGetValue(metadata.ColumnFamilyId, out var frontier) || frontier == ulong.MaxValue ||
            metadata.ColumnFamilyId != 0 && !manifest.ColumnFamilies.Any(family => family.Id == metadata.ColumnFamilyId && family.DeletedAt is null))
        {
            throw new PantsCorruptionException("An SST can be added only to a live column family with flush authority.");
        }

        var existing = manifest.Files.SingleOrDefault(file => file.Name == metadata.Name);
        if (existing is not null)
        {
            if (!SameIdentity(existing, metadata))
            {
                throw new PantsCorruptionException("An SST name has conflicting immutable identities.");
            }

            return;
        }

        manifest.Files.Add(metadata);
    }

    public static void Remove(ManifestState manifest, string name)
    {
        var metadata = manifest.Files.SingleOrDefault(file => file.Name == name);
        if (metadata is null)
        {
            return;
        }

        if (manifest.RetiredSsts.TryGetValue(name, out var prior) && !SameIdentity(prior, metadata))
        {
            throw new PantsCorruptionException("Retired SST has a conflicting identity.");
        }

        manifest.RetiredSsts[name] = metadata.Clone();
        manifest.Files.Remove(metadata);
    }

    public static void CompleteDelete(ManifestState manifest, FileMeta metadata)
    {
        if (!SstFileName.IsSafe(metadata.Name) || manifest.Files.Any(file => file.Name == metadata.Name) ||
            manifest.RetiredSsts.TryGetValue(metadata.Name, out var retired) && !SameIdentity(retired, metadata))
        {
            throw new PantsCorruptionException("SST deletion acknowledgement conflicts with current authority.");
        }

        manifest.RetiredSsts.Remove(metadata.Name);
    }

    public static void ValidateBatchFrontiers(ManifestState manifest, ManifestEdit edit, JsonSerializerOptions options)
    {
        if (edit.Variant != "Batch")
        {
            return;
        }

        var baselineNames = manifest.Files.Select(static file => file.Name).ToHashSet(StringComparer.Ordinal);
        var retainedNames = new HashSet<string>(baselineNames, StringComparer.Ordinal);
        var unavailableNames = manifest.RetiredSsts.Keys.ToHashSet(StringComparer.Ordinal);
        var added = new Dictionary<string, (FileMeta File, int Index)>(StringComparer.Ordinal);
        var frontiers = new Dictionary<uint, (ulong Sequence, int Index)>();
        var flattened = FlattenBatch(edit.ToElement(options).GetProperty("Batch")).ToArray();
        for (var index = 0; index < flattened.Length; index++)
        {
            var variant = flattened[index].EnumerateObject().Single();
            switch (variant.Name)
            {
                case "AddSst":
                    var file = variant.Value.Deserialize<FileMeta>(options)!;
                    if (unavailableNames.Contains(file.Name))
                    {
                        throw new PantsCorruptionException("A manifest batch cannot reuse an SST identity pending or removed during retirement.");
                    }

                    retainedNames.Add(file.Name);
                    if (!baselineNames.Contains(file.Name))
                    {
                        added.TryAdd(file.Name, (file, index));
                    }

                    break;
                case "RemoveSst":
                    var name = variant.Value.GetProperty("name").GetString()!;
                    unavailableNames.Add(name);
                    retainedNames.Remove(name);
                    break;
                case "ReclaimColumnFamily":
                    foreach (var removed in variant.Value.GetProperty("names").EnumerateArray())
                    {
                        var removedName = removed.GetString()!;
                        unavailableNames.Add(removedName);
                        retainedNames.Remove(removedName);
                    }

                    break;
                case "AdvanceFlushFrontier":
                    var id = variant.Value.GetProperty("cf_id").GetUInt32();
                    var sequence = variant.Value.GetProperty("freeze_sequence").GetUInt64();
                    if (!frontiers.TryAdd(id, (sequence, index)))
                    {
                        throw new PantsCorruptionException("A manifest batch cannot advance a family more than once.");
                    }

                    break;
            }
        }

        foreach (var (familyId, frontier) in frontiers)
        {
            var outputs = added.Values.Where(item => item.File.ColumnFamilyId == familyId).ToArray();
            if (!outputs.Any(item => item.Index < frontier.Index) ||
                outputs.Any(item => !retainedNames.Contains(item.File.Name) || !item.File.LargestSequence.HasValue || item.File.LargestSequence > frontier.Sequence))
            {
                throw new PantsCorruptionException("A flush frontier requires newly added, retained SSTs with known covered sequence bounds.");
            }
        }
    }

    static IEnumerable<JsonElement> FlattenBatch(JsonElement edits)
    {
        foreach (var edit in edits.EnumerateArray())
        {
            var variant = edit.EnumerateObject().Single();
            if (variant.Name == "Batch")
            {
                foreach (var child in FlattenBatch(variant.Value))
                {
                    yield return child;
                }
            }
            else
            {
                yield return edit;
            }
        }
    }

    public static void Move(ManifestState manifest, JsonElement value, JsonSerializerOptions options)
    {
        if (!value.TryGetProperty("captured", out var capturedValue) || capturedValue.ValueKind != JsonValueKind.Array ||
            !value.TryGetProperty("target_level", out var targetValue) || !targetValue.TryGetUInt32(out var target))
        {
            throw new PantsCorruptionException("Logical SST move is malformed.");
        }

        var captured = capturedValue.Deserialize<List<FileMeta>>(options)!;
        var current = ValidateMove(manifest.Files, manifest.ColumnFamilies, captured, target, out _);
        foreach (var file in current)
        {
            file.Level = target;
        }
    }

    public static FileMeta[] ValidateMove(
        IReadOnlyList<FileMeta> files, IReadOnlyList<ColumnFamilyMeta> families,
        IReadOnlyList<FileMeta> captured, uint target, out bool alreadyMoved)
    {
        if (captured.Count == 0 || captured.Select(static file => file.Name).Distinct(StringComparer.Ordinal).Count() != captured.Count)
        {
            throw new PantsCorruptionException("Logical SST move requires unique captured identities.");
        }

        var source = captured[0].Level;
        var familyId = captured[0].ColumnFamilyId;
        if (source == uint.MaxValue || target != source + 1 ||
            familyId != 0 && !families.Any(family => family.Id == familyId && family.DeletedAt is null) ||
            captured.Any(file => file.Level != source || file.ColumnFamilyId != familyId || !file.HasTrustedKeyBounds() ||
                                 !file.ContentCrc32C.HasValue || !SstFileName.IsSafe(file.Name)))
        {
            throw new PantsCorruptionException("Logical SST move has invalid level, family or coverage.");
        }

        var current = captured.Select(file => files.SingleOrDefault(candidate => candidate.Name == file.Name)).ToArray();
        alreadyMoved = current.All(file => file?.Level == target);
        for (var index = 0; index < captured.Count; index++)
        {
            var expected = captured[index].Clone();
            if (alreadyMoved)
            {
                expected.Level = target;
            }

            if (current[index] is not { } actual || !SameIdentity(actual, expected))
            {
                throw new PantsCorruptionException("Logical SST move no longer matches its captured identities.");
            }
        }

        for (var index = 0; index < captured.Count; index++)
        {
            var file = captured[index];
            if (captured.Skip(index + 1).Any(other => Overlaps(file, other)))
            {
                throw new PantsCorruptionException("Logical SST move captures overlapping files.");
            }

            foreach (var other in files.Where(other => other.ColumnFamilyId == familyId && !captured.Any(item => item.Name == other.Name)))
            {
                if (!Overlaps(file, other))
                {
                    continue;
                }

                if (other.Level == target || source != 0 && other.Level == source ||
                    source == 0 && other.Level == 0 &&
                    (!other.SmallestSequence.HasValue || !file.LargestSequence.HasValue || other.SmallestSequence <= file.LargestSequence))
                {
                    throw new PantsCorruptionException("Logical SST move crosses overlapping coverage.");
                }
            }
        }

        return current!;
    }

    static bool Overlaps(FileMeta left, FileMeta right) =>
        !left.HasTrustedKeyBounds() || !right.HasTrustedKeyBounds() ||
        left.SmallestKey.AsSpan().SequenceCompareTo(right.LargestKey) <= 0 && right.SmallestKey.AsSpan().SequenceCompareTo(left.LargestKey) <= 0;

    static bool SameBytes(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}
