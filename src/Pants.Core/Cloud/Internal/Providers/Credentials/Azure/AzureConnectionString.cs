namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.Azure;

sealed class AzureConnectionString
{
    const string AzuriteAccount = "devstoreaccount1";

    const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    public string? Account { get; init; }

    public string? AccountKey { get; init; }

    public string? SasToken { get; init; }

    public Uri? BlobEndpoint { get; init; }

    public string? Protocol { get; init; }

    public string? EndpointSuffix { get; init; }

    public static AzureConnectionString Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PantsInvalidArgumentException(
                "Azure Storage connection string must not be empty.");
        }

        var fields = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Split('=', 2))
            .Where(static pair => pair.Length == 2)
            .ToDictionary(
                static pair => pair[0].Trim(),
                static pair => pair[1].Trim(),
                StringComparer.OrdinalIgnoreCase);
        if (fields.GetValueOrDefault("UseDevelopmentStorage")
                ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new AzureConnectionString
            {
                Account = AzuriteAccount,
                AccountKey = AzuriteKey,
                BlobEndpoint = new Uri(
                    "http://127.0.0.1:10000/devstoreaccount1",
                    UriKind.Absolute)
            };
        }

        Uri? blobEndpoint = null;
        if (fields.GetValueOrDefault("BlobEndpoint") is { Length: > 0 } endpoint)
        {
            if (!Uri.TryCreate(endpoint.TrimEnd('/'), UriKind.Absolute, out blobEndpoint))
            {
                throw new PantsInvalidArgumentException(
                    "Azure Storage connection-string BlobEndpoint is invalid.");
            }
        }

        return new AzureConnectionString
        {
            Account = Nonempty(fields.GetValueOrDefault("AccountName")),
            AccountKey = Nonempty(fields.GetValueOrDefault("AccountKey")),
            SasToken = Nonempty(fields.GetValueOrDefault("SharedAccessSignature")),
            BlobEndpoint = blobEndpoint,
            Protocol = Nonempty(fields.GetValueOrDefault("DefaultEndpointsProtocol")),
            EndpointSuffix = Nonempty(fields.GetValueOrDefault("EndpointSuffix"))
        };
    }

    public Uri? CreateAccountEndpoint()
    {
        if (BlobEndpoint is not null)
        {
            return BlobEndpoint;
        }

        if (Account is null || EndpointSuffix is null)
        {
            return null;
        }

        var protocol = Protocol ?? "https";
        return new Uri(
            $"{protocol}://{Account}.blob.{EndpointSuffix.TrimStart('.')}",
            UriKind.Absolute);
    }

    public override string ToString() => "AzureConnectionString { Value = [REDACTED] }";

    static string? Nonempty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
