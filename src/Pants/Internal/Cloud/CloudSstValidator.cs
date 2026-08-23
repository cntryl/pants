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

        SstManifestMetadataValidator.Validate(contents, file, "Cloud SST");
    }
}
