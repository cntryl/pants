namespace Cntryl.Pants.Support.Failpoints;

sealed class CloudWalSealLeaseLossFailpointHandler(string root) : IFailpointHandler
{
    public void Hit(Failpoint failpoint)
    {
        if (failpoint != Failpoint.AfterCloudWalSealFlush)
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(root, ".midge_leader"),
            $"epoch: {ulong.MaxValue}\nholder_id: replacement-writer\n" +
            $"acquired_at: {DateTimeOffset.UtcNow:O}\n");
    }
}
