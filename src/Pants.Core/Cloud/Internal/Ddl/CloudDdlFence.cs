namespace Cntryl.Pants.Cloud.Internal.Ddl;

static class CloudDdlFence
{
    public static byte[] Encode(ReadOnlySpan<byte> registryBytes, ulong writerEpoch)
    {
        if (writerEpoch == 0)
        {
            throw new PantsFencedException(
                "Cloud DDL fencing requires a non-zero writer epoch.");
        }

        var registry = CloudDdlJson.DeserializeRegistry(registryBytes);
        if (registry.WriterEpoch > writerEpoch)
        {
            throw new PantsFencedException("The cloud DDL registry is owned by a successor.");
        }

        registry.WriterEpoch = writerEpoch;
        return CloudDdlJson.SerializeRegistry(registry);
    }
}
