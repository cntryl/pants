namespace Cntryl.Pants.Cloud.Internal;

sealed class CloudPreflightException : Exception
{
    public CloudPreflightException(PantsCloudFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public PantsCloudFailureKind FailureKind { get; }
}
