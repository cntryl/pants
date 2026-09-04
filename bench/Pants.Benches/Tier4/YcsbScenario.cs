namespace Cntryl.Pants.Tier4;

public sealed record YcsbScenario(Tier4StorageMode StorageMode, int Clients)
{
    public override string ToString() => $"{StorageMode}-{Clients}";
}
