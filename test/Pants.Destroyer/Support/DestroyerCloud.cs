namespace Cntryl.Pants.Destroyer.Support;

/// <summary>
/// Shared support for scenarios that exercise Pants's simulated-cloud
/// storage mode (<see cref="PantsOpenOptions.SimulatedCloud"/>) — an
/// in-process cloud object store, standing in for midge-destroyer's Sqrzl
/// emulator so cloud-only scenarios don't need an external service.
/// </summary>
public static class DestroyerCloud
{
    public static PantsOpenOptions CreateOptions(string path, string prefix, long localBudgetBytes) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-destroyer", prefix)
            .WithSimulatedCloudLocalStorageBudget(localBudgetBytes)
            .WithBackgroundCompaction(false);

    public static byte[] CreateValue(int length, int seed)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    public static string[] LocalSsts(string root) =>
        Directory.GetFiles(Path.Combine(root, "sst"), "*.sst");

    public static string[] CloudSsts(string root) =>
        Directory.GetFiles(Path.Combine(root, "cloud_store", "sst"), "*.sst");
}
