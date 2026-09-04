namespace Cntryl.Pants.Cloud.Internal;

static class CloudSstValidator
{
    public static void Validate(ReadOnlyMemory<byte> data, FileMeta file)
    {
        if ((file.SizeBytes != 0 && checked((ulong)data.Length) != file.SizeBytes) ||
            (file.ContentCrc32C.HasValue &&
             DiskFormat.Crc32C(data.Span) != file.ContentCrc32C.Value))
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{file.Name}' differs from its manifest publication proof.");
        }

        SstContents contents;
        try
        {
            contents = SstCodec.Decode(data.ToArray());
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
