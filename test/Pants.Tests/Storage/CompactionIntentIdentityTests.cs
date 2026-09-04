namespace Cntryl.Pants.Storage;

public sealed class CompactionIntentIdentityTests
{
    [Fact]
    public void ShouldRejectManifestPublishedOutputOwnedByDifferentInputsAsCorruption()
    {
        var original = CompactionIntentIdentity.Create(0, ["a.sst", "b.sst"], ["c.sst"]);
        var replacement = CompactionIntentIdentity.Create(0, ["different.sst"], ["c.sst"]);

        var exception = Assert.Throws<PantsCorruptionException>(() =>
            original.ValidateReplacement(replacement, "ManifestPublished"));

        Assert.Contains("different inputs", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["c.sst"], original.GetAddedFileNames());
    }

    [Fact]
    public void ShouldFenceReplacementOfManifestPublishedInputs()
    {
        var original = CompactionIntentIdentity.Create(0, ["a.sst", "b.sst"], ["c.sst"]);
        var replacement = CompactionIntentIdentity.Create(0, ["b.sst", "a.sst"], ["new.sst"]);

        var exception = Assert.Throws<PantsBusyException>(() =>
            original.ValidateReplacement(replacement, "ManifestPublished"));

        Assert.Contains("cannot be replaced", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["c.sst"], original.GetAddedFileNames());
    }
}
