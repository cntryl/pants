namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>A fact that runs only when <c>PANTS_CRASH_SOAK=1</c>, so the default suite and CI skip the soak.</summary>
sealed class CrashSoakFactAttribute : FactAttribute
{
    public CrashSoakFactAttribute()
    {
        if (!CrashSoakSettings.Enabled)
        {
            Skip = $"Opt-in soak; set {CrashSoakSettings.EnabledVariable}=1 to run it (see docs/testing/crash-soak.md).";
        }
    }
}
