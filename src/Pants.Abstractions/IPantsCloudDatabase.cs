namespace Cntryl.Pants;

public interface IPantsCloudDatabase
{
    bool IsPrimaryLeaseHealthy { get; }
}
