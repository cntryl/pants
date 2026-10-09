namespace Cntryl.Pants.Storage;

/// <summary>
///     The classified result of a storage verification: either a completed
///     <see cref="Report" />, or a failure described by <see cref="ErrorCode" />, an optional
///     <see cref="PathFailure" /> and a <see cref="Message" />.
/// </summary>
public sealed record PantsStorageVerificationOutcome(
    PantsStorageVerificationOutcomeKind Kind,
    PantsStorageVerificationReport? Report,
    PantsErrorCode? ErrorCode,
    PantsStoragePathFailure? PathFailure,
    string? Message)
{
    /// <summary>The reference engine's <c>midge verify</c> exit code for this outcome.</summary>
    public int ExitCode => (int)Kind;

    /// <summary>Classifies a completed verification report by its health.</summary>
    public static PantsStorageVerificationOutcome FromReport(PantsStorageVerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new PantsStorageVerificationOutcome(
            report.Health.GetVerificationOutcomeKind(),
            report,
            null,
            null,
            null);
    }

    /// <summary>Classifies a verification failure, such as one thrown by online verification.</summary>
    public static PantsStorageVerificationOutcome FromException(PantsException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new PantsStorageVerificationOutcome(
            exception.Code.GetVerificationOutcomeKind(),
            null,
            exception.Code,
            null,
            exception.Message);
    }

    internal static PantsStorageVerificationOutcome FromPathFailure(
        PantsStoragePathFailure failure,
        string message) =>
        new(
            PantsStorageVerificationOutcomeKind.Storage,
            null,
            failure switch
            {
                PantsStoragePathFailure.Missing => PantsErrorCode.NotFound,
                PantsStoragePathFailure.Inaccessible => PantsErrorCode.Io,
                _ => PantsErrorCode.InvalidPath
            },
            failure,
            message);
}
