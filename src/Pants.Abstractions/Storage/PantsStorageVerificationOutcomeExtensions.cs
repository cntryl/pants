namespace Cntryl.Pants.Storage;

public static class PantsStorageVerificationOutcomeExtensions
{
    /// <summary>Classifies a completed verification report by its health.</summary>
    public static PantsStorageVerificationOutcomeKind GetVerificationOutcomeKind(this PantsEngineHealth health) =>
        health switch
        {
            PantsEngineHealth.Healthy => PantsStorageVerificationOutcomeKind.Healthy,
            PantsEngineHealth.Degraded or
                PantsEngineHealth.SalvageMode or
                PantsEngineHealth.WriteStalled => PantsStorageVerificationOutcomeKind.Degraded,
            PantsEngineHealth.Corrupt => PantsStorageVerificationOutcomeKind.Corruption,
            _ => PantsStorageVerificationOutcomeKind.Internal
        };

    /// <summary>
    ///     Classifies a verification failure. Missing data, invalid paths, and unknown write
    ///     outcomes that need reconciliation are storage outcomes; everything else follows
    ///     <see cref="PantsErrorCodeExtensions.GetSeverity" />, with backpressure reported as
    ///     degraded because it is an operational condition to retry.
    /// </summary>
    public static PantsStorageVerificationOutcomeKind GetVerificationOutcomeKind(this PantsErrorCode code) =>
        code is PantsErrorCode.NotFound or
            PantsErrorCode.InvalidPath or
            PantsErrorCode.CommitOutcomeUnknown
            ? PantsStorageVerificationOutcomeKind.Storage
            : code.GetSeverity() switch
            {
                PantsErrorSeverity.Caller => PantsStorageVerificationOutcomeKind.Usage,
                PantsErrorSeverity.Transient or
                    PantsErrorSeverity.Fenced => PantsStorageVerificationOutcomeKind.Storage,
                PantsErrorSeverity.Backpressure => PantsStorageVerificationOutcomeKind.Degraded,
                PantsErrorSeverity.Fatal => PantsStorageVerificationOutcomeKind.Corruption,
                _ => PantsStorageVerificationOutcomeKind.Internal
            };
}
