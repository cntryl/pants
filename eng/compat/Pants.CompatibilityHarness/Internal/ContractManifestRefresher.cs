using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pants.CompatibilityHarness.Internal;

internal static class ContractManifestRefresher
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static void Write(
        string currentManifestPath,
        string midgeCheckoutPath,
        string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentManifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(midgeCheckoutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var root = JsonNode.Parse(File.ReadAllBytes(currentManifestPath))?.AsObject()
            ?? throw new InvalidDataException("The committed Midge contract manifest is empty.");
        if (root["schemaVersion"]?.GetValue<int>() != 2)
        {
            throw new InvalidDataException("The committed Midge contract manifest schema is unsupported.");
        }

        var entries = root["entries"]?.AsArray()
            ?? throw new InvalidDataException("The committed Midge contract manifest has no entries.");
        if (entries.Count == 0)
        {
            throw new InvalidDataException("The committed Midge contract manifest is empty.");
        }

        var sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entryNode in entries)
        {
            var entry = entryNode?.AsObject()
                ?? throw new InvalidDataException("The Midge contract manifest contains a null entry.");
            var source = RequiredString(entry, "source");
            var symbol = RequiredString(entry, "sourceSymbolOrTest");
            var sourcePath = ResolveContainedPath(midgeCheckoutPath, source);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"Midge contract source '{source}' does not exist in the pinned checkout.",
                    sourcePath);
            }

            var sourceText = File.ReadAllText(sourcePath);
            if (!sourceText.Contains(symbol, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Midge contract symbol '{symbol}' was not found in '{source}'.");
            }

            _ = sources.Add(source);
        }

        var refreshed = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["midgeSha"] = MidgeCheckoutBuilder.RequiredCommit,
            ["sourceTreeSha256"] = ComputeSourceTreeHash(midgeCheckoutPath, sources),
            ["sourcePriority"] = root["sourcePriority"]?.DeepClone(),
            ["entries"] = entries.DeepClone()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(refreshed, JsonOptions);
        File.WriteAllBytes(destinationPath, [.. bytes, (byte)'\n']);
    }

    static string ComputeSourceTreeHash(
        string midgeCheckoutPath,
        IEnumerable<string> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var source in sources)
        {
            var sourceBytes = Encoding.UTF8.GetBytes(source);
            AppendLength(hash, sourceBytes.Length);
            hash.AppendData(sourceBytes);

            var content = File.ReadAllBytes(ResolveContainedPath(midgeCheckoutPath, source));
            AppendLength(hash, content.Length);
            hash.AppendData(content);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string RequiredString(JsonObject value, string propertyName)
    {
        var result = value[propertyName]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(result)
            ? throw new InvalidDataException(
                $"The Midge contract manifest entry has no '{propertyName}'.")
            : result;
    }

    static string ResolveContainedPath(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                $"Midge contract source path '{relativePath}' must be relative.");
        }

        var normalizedRoot = Path.GetFullPath(rootPath) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(normalizedRoot, comparison))
        {
            throw new InvalidDataException(
                $"Midge contract source path '{relativePath}' escapes the checkout.");
        }

        return fullPath;
    }

    static void AppendLength(IncrementalHash hash, long length)
    {
        var encoded = (Span<byte>)stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, length);
        hash.AppendData(encoded);
    }
}
