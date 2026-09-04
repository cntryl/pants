namespace Cntryl.Pants.Support.TestDoubles;

sealed class DelegatingCloudProvider(
    Func<CancellationToken, ValueTask<IPantsCloudObjectStore>> open) : IPantsCloudProvider
{
    public PantsCloudProviderId Id { get; } = new("test-provider");

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken OpenToken { get; private set; }

    public PantsCloudValidationReport Validate() => new([]);

    public async ValueTask<IPantsCloudObjectStore> OpenObjectStoreAsync(
        PantsCloudProviderContext context,
        CancellationToken cancellationToken = default)
    {
        OpenToken = cancellationToken;
        Started.TrySetResult();
        try
        {
            return await open(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Finished.TrySetResult();
        }
    }
}
