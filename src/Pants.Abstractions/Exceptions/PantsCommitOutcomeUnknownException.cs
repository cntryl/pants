namespace Cntryl.Pants.Exceptions;

/// <summary>
///     The commit was admitted to the runtime, but Pants could not determine whether it became
///     durable. Reopen or otherwise reconcile the database before retrying the mutation.
/// </summary>
public sealed class PantsCommitOutcomeUnknownException(
    string message,
    Exception? innerException = null)
    : PantsException(PantsErrorCode.CommitOutcomeUnknown, message, innerException);
