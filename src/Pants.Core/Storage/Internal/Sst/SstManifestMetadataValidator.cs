namespace Cntryl.Pants.Storage.Internal.Sst;

static class SstManifestMetadataValidator
{
    public static void ValidateMetadata(
        SstMetadata metadata,
        FileMeta file,
        string storageKind)
    {
        if (!file.HasTrustedKeyBounds())
        {
            return;
        }

        ValidateKey(storageKind, file.Name, "smallest", file.SmallestKey, metadata.SmallestKey);
        ValidateKey(storageKind, file.Name, "largest", file.LargestKey, metadata.LargestKey);
    }

    public static void Validate(
        SstContents contents,
        FileMeta file,
        string storageKind)
    {
        var keys = contents.Entries.Select(static entry => entry.Key)
            .Concat(contents.RangeTombstones.SelectMany(static range =>
                new[] { range.Start, range.End }))
            .OrderBy(static key => key, ByteArrayComparer.Instance)
            .ToArray();
        var sequences = contents.Entries.Select(static entry => entry.Sequence)
            .Concat(contents.RangeTombstones.Select(static range => range.Sequence))
            .ToArray();

        if (file.HasTrustedKeyBounds())
        {
            ValidateKey(storageKind, file.Name, "smallest", file.SmallestKey, keys.FirstOrDefault());
            ValidateKey(storageKind, file.Name, "largest", file.LargestKey, keys.LastOrDefault());
        }
        ValidateSequence(
            storageKind,
            file.Name,
            "smallest",
            file.SmallestSequence,
            sequences.Length == 0 ? null : sequences.Min());
        ValidateSequence(
            storageKind,
            file.Name,
            "largest",
            file.LargestSequence,
            sequences.Length == 0 ? null : sequences.Max());
    }

    static void ValidateKey(
        string storageKind,
        string fileName,
        string boundary,
        byte[]? expected,
        byte[]? actual)
    {
        if (expected is null ? actual is not null : actual is null || !actual.AsSpan().SequenceEqual(expected))
        {
            throw new PantsCorruptionException(
                $"{storageKind} '{fileName}' {boundary} key differs from its manifest.");
        }
    }

    static void ValidateSequence(
        string storageKind,
        string fileName,
        string boundary,
        ulong? expected,
        ulong? actual)
    {
        if (expected.HasValue && actual != expected)
        {
            throw new PantsCorruptionException(
                $"{storageKind} '{fileName}' {boundary} sequence differs from its manifest.");
        }
    }
}
