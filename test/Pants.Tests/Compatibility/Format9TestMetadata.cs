using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

static class Format9TestMetadata
{
    public static TemporaryDirectory CopyFixture(string name)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "LsmSpec", "7748fd1", "conformance", "fixtures", "format9", name);
        var directory = new TemporaryDirectory();
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(directory.Path, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }

        return directory;
    }

    public static JsonDocument Read(string path) => MetadataFrameCodec.Decode(File.ReadAllBytes(path), 1);

    public static JsonNode ReadNode(string path)
    {
        using var document = Read(path);
        return JsonNode.Parse(document.RootElement.GetRawText())!;
    }

    public static Task WriteAsync(string path, string json)
    {
        var manifest = JsonNode.Parse(json)!.AsObject();
        var families = manifest["column_families"]?.AsArray();
        if (families is not null)
        {
            foreach (var family in families.Where(item => item?["id"]?.GetValue<uint>() == 0).ToArray())
            {
                families.Remove(family);
            }
        }

        manifest["flushed_through"] ??= new JsonArray(new JsonObject { ["cf_id"] = 0U, ["through_sequence"] = 0UL });
        if (families is not null)
        {
            foreach (var family in families)
            {
                var id = family!["id"]!.GetValue<uint>();
                if (!manifest["flushed_through"]!.AsArray().Any(row => row!["cf_id"]!.GetValue<uint>() == id))
                {
                    manifest["flushed_through"]!.AsArray().Add(new JsonObject { ["cf_id"] = id, ["through_sequence"] = family["deleted_at"] is null ? 0UL : ulong.MaxValue });
                }
            }
        }

        return File.WriteAllBytesAsync(path, MetadataFrameCodec.Encode(1, Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }
}
