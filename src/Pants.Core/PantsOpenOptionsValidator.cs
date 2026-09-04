namespace Cntryl.Pants;

/// <summary>Validates that open options can be resolved into an executable runtime plan.</summary>
public static class PantsOpenOptionsValidator
{
    public static void Validate(PantsOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = RuntimePlan.Resolve(options);
    }
}
