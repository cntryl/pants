namespace Cntryl.Pants;

internal static class AzureCredentialResolver
{
    public static AzureCredentialResolution Resolve(
        PantsCloudProviderConfiguration.AzureBlob configuration,
        HttpClient httpClient,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.Credential switch
        {
            PantsAzureCredentialSource.SharedKey sharedKey => Static(
                RequireAccount(configuration.Account),
                ResolveEndpoint(configuration.Account, configuration.Endpoint),
                AzureResolvedCredential.SharedKey(RequireSecret(sharedKey.AccountKey))),
            PantsAzureCredentialSource.SasToken sas => Static(
                configuration.Account,
                ResolveEndpoint(configuration.Account, configuration.Endpoint),
                AzureResolvedCredential.Sas(RequireSecret(sas.Token))),
            PantsAzureCredentialSource.ConnectionString connection => FromConnectionString(
                configuration,
                connection.Value),
            PantsAzureCredentialSource.StorageEnvironment => FromStorageEnvironment(
                configuration,
                httpClient,
                timeout),
            PantsAzureCredentialSource.EnvironmentClientSecret or
            PantsAzureCredentialSource.WorkloadIdentity or
            PantsAzureCredentialSource.ManagedIdentity or
            PantsAzureCredentialSource.LightweightDefaultChain => Identity(
                configuration,
                httpClient,
                timeout),
            _ => throw new PantsNotSupportedException(
                "The Azure credential source is unsupported.")
        };
    }

    static AzureCredentialResolution FromConnectionString(
        PantsCloudProviderConfiguration.AzureBlob configuration,
        string value)
    {
        var parsed = AzureConnectionString.Parse(value);
        var account = parsed.Account ?? configuration.Account;
        var endpoint = configuration.Endpoint ??
            parsed.CreateAccountEndpoint() ??
            ResolveEndpoint(account, endpoint: null);
        if (parsed.AccountKey is { } key)
        {
            return Static(
                RequireAccount(account),
                endpoint,
                AzureResolvedCredential.SharedKey(RequireSecret(key)));
        }

        if (parsed.SasToken is { } sas)
        {
            if (string.IsNullOrWhiteSpace(account) && parsed.BlobEndpoint is null &&
                configuration.Endpoint is null)
            {
                throw new PantsInvalidArgumentException(
                    "Azure SAS connection string must include AccountName or BlobEndpoint.");
            }

            return Static(
                account,
                endpoint,
                AzureResolvedCredential.Sas(RequireSecret(sas)));
        }

        throw new PantsInvalidArgumentException(
            "Azure Storage connection string must contain AccountKey or SharedAccessSignature.");
    }

    static AzureCredentialResolution FromStorageEnvironment(
        PantsCloudProviderConfiguration.AzureBlob configuration,
        HttpClient httpClient,
        TimeSpan timeout)
    {
        var connection = Environment.GetEnvironmentVariable(
            "AZURE_STORAGE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(connection))
        {
            return FromConnectionString(configuration, connection);
        }

        var account = string.IsNullOrWhiteSpace(configuration.Account)
            ? Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT")
            : configuration.Account;
        account = RequireAccount(account);
        var endpoint = ResolveEndpoint(account, configuration.Endpoint);
        var key = Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            return Static(
                account,
                endpoint,
                AzureResolvedCredential.SharedKey(key));
        }

        var sas = Environment.GetEnvironmentVariable("AZURE_STORAGE_SAS_TOKEN");
        if (!string.IsNullOrWhiteSpace(sas))
        {
            return Static(account, endpoint, AzureResolvedCredential.Sas(sas));
        }

        return new AzureCredentialResolution(
            account,
            ValidateIdentityEndpoint(endpoint),
            AzureResolvedCredential.Bearer(new RefreshingAzureTokenProvider(
                new PantsAzureCredentialSource.ManagedIdentity(),
                endpoint,
                httpClient,
                timeout)));
    }

    static AzureCredentialResolution Identity(
        PantsCloudProviderConfiguration.AzureBlob configuration,
        HttpClient httpClient,
        TimeSpan timeout)
    {
        var account = RequireAccount(configuration.Account);
        var endpoint = ValidateIdentityEndpoint(
            ResolveEndpoint(account, configuration.Endpoint));
        return new AzureCredentialResolution(
            account,
            endpoint,
            AzureResolvedCredential.Bearer(new RefreshingAzureTokenProvider(
                configuration.Credential,
                endpoint,
                httpClient,
                timeout)));
    }

    static AzureCredentialResolution Static(
        string account,
        Uri endpoint,
        AzureResolvedCredential credential) => new(account, endpoint, credential);

    static Uri ResolveEndpoint(string? account, Uri? endpoint) => endpoint ?? new Uri(
        $"https://{RequireAccount(account)}.blob.core.windows.net",
        UriKind.Absolute);

    static Uri ValidateIdentityEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            endpoint.AbsolutePath != "/")
        {
            throw new PantsInvalidArgumentException(
                "Azure identity credential Blob endpoint must be an absolute HTTPS origin without credentials, path, query, or fragment.");
        }

        return endpoint;
    }

    static string RequireAccount(string? account) =>
        string.IsNullOrWhiteSpace(account)
            ? throw new PantsInvalidArgumentException(
                "Azure Storage account name is required.")
            : account;

    static string RequireSecret(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new PantsInvalidArgumentException(
                "Azure credential value must not be empty.")
            : value;
}
