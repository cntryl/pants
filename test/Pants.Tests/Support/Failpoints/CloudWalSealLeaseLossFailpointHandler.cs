namespace Cntryl.Pants.Tests.Support.Failpoints;

sealed class CloudWalSealLeaseLossFailpointHandler(string root) : IPantsFailpointHandler
{
    public void Hit(PantsFailpoint failpoint)
    {
        if (failpoint != PantsFailpoint.AfterCloudWalSealFlush)
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(root, ".midge_leader"),
            $"epoch: {ulong.MaxValue}\nholder_id: replacement-writer\n" +
            $"acquired_at: {DateTimeOffset.UtcNow:O}\n");
    }
}
