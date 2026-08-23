namespace Pants;

static class CloudSstObjectKey
{
    public static bool TryGetName(string objectKey, out string name)
    {
        name = string.Empty;
        if (!objectKey.StartsWith(PantsCloudObjectLayout.SstPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = objectKey[PantsCloudObjectLayout.SstPrefix.Length..];
        if (string.IsNullOrEmpty(candidate) ||
            candidate != Path.GetFileName(candidate) ||
            !candidate.EndsWith(".sst", StringComparison.Ordinal) ||
            candidate.Contains(':') ||
            candidate.Contains('\\'))
        {
            return false;
        }

        name = candidate;
        return true;
    }
}
