using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class ManifestEditValidationTests
{
    [Theory]
    [InlineData("../escape.sst")]
    [InlineData("evil\0.sst")]
    public async Task ShouldRejectUnsafeSstNameBeforeJournalAppendGivenDropEdit(string unsafeName)
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        var journalPath = Path.Combine(directory.Path, "manifest.journal");
        var json = new JsonObject
        {
            ["DropColumnFamilyAt"] = new JsonObject
            {
                ["id"] = 7,
                ["drop_sequence"] = 1,
                ["dropped_sst_names"] = new JsonArray(unsafeName)
            }
        }.ToJsonString();
        using var document = JsonDocument.Parse(json);
        var edit = document.RootElement.Clone();

        using (var store = LocalDiskStore.Open(directory.Path, state))
        {
            var journalBefore = ReadJournal(journalPath);

            var exception = Assert.ThrowsAny<PantsException>(() =>
                store.AdoptRemoteCommittedColumnFamilyEdit(state, edit));

            Assert.Equal(PantsErrorCode.InvalidArgument, exception.Code);
            Assert.Equal(journalBefore, ReadJournal(journalPath));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        Assert.Null(await reopened.ColumnFamilies.GetAsync("never-created"));
    }

    static byte[] ReadJournal(string path) => File.Exists(path) ? File.ReadAllBytes(path) : [];
}
