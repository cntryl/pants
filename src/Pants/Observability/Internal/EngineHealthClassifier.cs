namespace Pants;

static class EngineHealthClassifier
{
    public static PantsEngineHealth Classify(
        PantsEngineHealth storageHealth,
        bool writeStalled) =>
        storageHealth switch
        {
            PantsEngineHealth.SalvageMode => PantsEngineHealth.SalvageMode,
            _ when writeStalled => PantsEngineHealth.WriteStalled,
            _ => storageHealth
        };
}
