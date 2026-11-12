using System.Text.Json.Nodes;

namespace Cntryl.Pants.Compatibility;

public sealed class Format9ManifestAdmissionTests
{
    [Theory]
    [InlineData("default-name")]
    [InlineData("duplicate-name")]
    [InlineData("reclaimed-live")]
    [InlineData("missing-drop-sequence")]
    [InlineData("live-drop-sequence")]
    [InlineData("live-dropped-files")]
    [InlineData("unsafe-dropped-file")]
    [InlineData("null-dropped-files")]
    [InlineData("missing-name")]
    [InlineData("orphan-allocator")]
    [InlineData("orphan-retired-file")]
    public async Task ShouldRejectInconsistentFamilyLifecycleWithoutChangingSnapshot(string fault)
    {
        using var directory = Format9TestMetadata.CopyFixture("empty-local");
        var path = Path.Combine(directory.Path, "manifest.snapshot.json");
        var snapshot = Format9TestMetadata.ReadNode(path);
        var family = new JsonObject { ["id"] = 1, ["name"] = "family", ["created_at"] = 0 };
        var frontier = new JsonObject { ["cf_id"] = 1, ["through_sequence"] = 0UL };
        snapshot["column_families"] = new JsonArray(family);
        snapshot["flushed_through"]!.AsArray().Add(frontier);
        switch (fault)
        {
            case "default-name": family["name"] = "default"; break;
            case "duplicate-name":
                snapshot["column_families"]!.AsArray().Add(new JsonObject { ["id"] = 2, ["name"] = "family" });
                snapshot["flushed_through"]!.AsArray().Add(new JsonObject { ["cf_id"] = 2, ["through_sequence"] = 0UL });
                break;
            case "reclaimed-live": family["reclaimed"] = true; break;
            case "missing-drop-sequence":
                family["deleted_at"] = 0;
                frontier["through_sequence"] = ulong.MaxValue;
                break;
            case "live-drop-sequence": family["drop_sequence"] = 1; break;
            case "live-dropped-files": family["dropped_sst_names"] = new JsonArray("source.sst"); break;
            case "unsafe-dropped-file":
                family["deleted_at"] = 0;
                family["drop_sequence"] = 1;
                family["dropped_sst_names"] = new JsonArray("../source.sst");
                frontier["through_sequence"] = ulong.MaxValue;
                break;
            case "null-dropped-files": family["dropped_sst_names"] = null; break;
            case "missing-name": family.Remove("name"); break;
            case "orphan-allocator": snapshot["next_sst_seqs"] = new JsonObject { ["2"] = 1UL }; break;
            case "orphan-retired-file":
                snapshot["retired_ssts"] = new JsonObject
                {
                    ["retired.sst"] = new JsonObject { ["name"] = "retired.sst", ["level"] = 0, ["size_bytes"] = 1UL, ["cf_id"] = 2 }
                };
                break;
        }

        await Format9TestMetadata.WriteAsync(path, snapshot.ToJsonString());
        var corrupted = await File.ReadAllBytesAsync(path);

        foreach (var policy in new[] { PantsRecoveryPolicy.Strict, PantsRecoveryPolicy.Salvage })
        {
            await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(directory.Path).WithRecoveryPolicy(policy)).AsTask());
            Assert.Equal(corrupted, await File.ReadAllBytesAsync(path));
        }
    }
}
