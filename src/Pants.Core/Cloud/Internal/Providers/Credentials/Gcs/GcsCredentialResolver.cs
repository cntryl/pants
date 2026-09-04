namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.Gcs;

static class GcsCredentialResolver
{
    public static GcsCredential Resolve(
        PantsGcsCredentialSource source,
        HttpClient httpClient,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return source switch
        {
            PantsGcsCredentialSource.BearerToken bearer => new GcsCredential(
                null,
                null,
                new StaticGcsTokenProvider(bearer.Token)),
            PantsGcsCredentialSource.HmacKey hmac => new GcsCredential(
                Require(hmac.AccessId, "GCS HMAC access ID"),
                Require(hmac.Secret, "GCS HMAC secret"),
                null),
            PantsGcsCredentialSource.ApplicationDefault or
                PantsGcsCredentialSource.ServiceAccountJsonFile or
                PantsGcsCredentialSource.AuthorizedUserJsonFile or
                PantsGcsCredentialSource.MetadataServer => new GcsCredential(
                    null,
                    null,
                    new RefreshingGcsTokenProvider(httpClient, source, timeout, timeProvider)),
            _ => throw new PantsNotSupportedException("The GCS credential source is unsupported.")
        };
    }

    static string Require(string value, string description) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new PantsInvalidArgumentException($"{description} must not be empty.")
            : value;
}
