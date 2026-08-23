namespace Cntryl.Pants.Observability;

public enum PantsEngineHealth
{
    Healthy,
    Degraded,
    SalvageMode,
    WriteStalled,
    Corrupt
}
