namespace Pants;

static class CloudSstValidator
{
    public static void Validate(ReadOnlyMemory<byte> data, MidgeFileMeta file)
    {
        if ((file.SizeBytes != 0 && checked((ulong)data.Length) != file.SizeBytes) ||
            (file.ContentCrc32C.HasValue &&
             MidgeDiskFormat.Crc32C(data.Span) != file.ContentCrc32C.Value))
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{file.Name}' differs from its manifest publication proof.");
        }

        MidgeSstContents contents;
        try
        {
            contents = MidgeSstCodec.Decode(data.ToArray());
        }
        catch (PantsException exception)
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{file.Name}' is structurally corrupt.",
                exception);
        }

        var keys = contents.Entries.Select(static entry => entry.Key)
            .Concat(contents.RangeTombstones.SelectMany(static range =>
                new[] { range.Start, range.End }))
            .OrderBy(static key => key, ByteArrayComparer.Instance)
            .ToArray();
        var sequences = contents.Entries.Select(static entry => entry.Sequence)
            .Concat(contents.RangeTombstones.Select(static range => range.Sequence))
            .ToArray();
        ValidateKey(file.Name, "smallest", file.SmallestKey, keys.FirstOrDefault());
        ValidateKey(file.Name, "largest", file.LargestKey, keys.LastOrDefault());
        if ((file.SmallestSequence.HasValue &&
             sequences.DefaultIfEmpty().Min() != file.SmallestSequence.Value) ||
            (file.LargestSequence.HasValue &&
             sequences.DefaultIfEmpty().Max() != file.LargestSequence.Value))
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{file.Name}' sequence range differs from its manifest.");
        }
    }

    static void ValidateKey(
        string fileName,
        string boundary,
        int[]? expected,
        byte[]? actual)
    {
        if (expected is null)
        {
            if (actual is null)
            {
                return;
            }

            throw new PantsCorruptionException(
                $"Cloud SST '{fileName}' {boundary} key differs from its manifest.");
        }

        var expectedBytes = new byte[expected.Length];
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] is < byte.MinValue or > byte.MaxValue)
            {
                throw new PantsCorruptionException(
                    $"Cloud SST '{fileName}' has an invalid manifest key byte.");
            }

            expectedBytes[index] = (byte)expected[index];
        }

        if (actual is null || !actual.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{fileName}' {boundary} key differs from its manifest.");
        }
    }
}
