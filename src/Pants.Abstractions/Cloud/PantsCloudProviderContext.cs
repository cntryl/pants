namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudProviderContext
{
    public PantsCloudProviderContext(
        string prefix,
        TimeSpan operationTimeout,
        HttpClient storageHttpClient,
        HttpClient credentialHttpClient,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(storageHttpClient);
        ArgumentNullException.ThrowIfNull(credentialHttpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (operationTimeout < TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                "The operation timeout must be at least one millisecond.");
        }

        Prefix = prefix;
        OperationTimeout = operationTimeout;
        StorageHttpClient = storageHttpClient;
        CredentialHttpClient = credentialHttpClient;
        TimeProvider = timeProvider;
    }

    public string Prefix { get; }

    public TimeSpan OperationTimeout { get; }

    public HttpClient StorageHttpClient { get; }

    public HttpClient CredentialHttpClient { get; }

    public TimeProvider TimeProvider { get; }
}
