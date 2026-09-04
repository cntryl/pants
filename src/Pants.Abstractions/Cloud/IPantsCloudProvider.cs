namespace Cntryl.Pants.Cloud;

public interface IPantsCloudProvider
{
    PantsCloudProviderId Id { get; }

    PantsCloudValidationReport Validate();

    ValueTask<IPantsCloudObjectStore> OpenObjectStoreAsync(
        PantsCloudProviderContext context,
        CancellationToken cancellationToken = default);
}
