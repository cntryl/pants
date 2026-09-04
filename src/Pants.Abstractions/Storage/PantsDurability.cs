namespace Cntryl.Pants.Storage;

public enum PantsDurability
{
    Sync,
    Buffered,
    BestEffort,
    CloudAsync,
    CloudStrict
}
