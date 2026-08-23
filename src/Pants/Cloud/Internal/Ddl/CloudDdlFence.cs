namespace Cntryl.Pants;

static class CloudDdlFence
{
    const int EpochBitCount = sizeof(ulong) * 8;

    public static byte[] Encode(ReadOnlySpan<byte> registryBytes, ulong writerEpoch)
    {
        if (writerEpoch == 0)
        {
            throw new PantsFencedException(
                "Cloud DDL fencing requires a non-zero writer epoch.");
        }

        _ = CloudDdlJson.DeserializeRegistry(registryBytes);
        var contentLength = registryBytes.Length;
        while (contentLength > 0 && IsJsonWhitespace(registryBytes[contentLength - 1]))
        {
            contentLength--;
        }

        var fenced = new byte[contentLength + EpochBitCount + 2];
        registryBytes[..contentLength].CopyTo(fenced);
        fenced[contentLength] = (byte)'\n';
        for (var bit = 0; bit < EpochBitCount; bit++)
        {
            fenced[contentLength + bit + 1] =
                (writerEpoch & (1UL << bit)) == 0 ? (byte)' ' : (byte)'\t';
        }

        fenced[^1] = (byte)'\n';
        return fenced;
    }

    static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
