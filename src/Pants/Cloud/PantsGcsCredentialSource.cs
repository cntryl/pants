namespace Cntryl.Pants.Cloud;

public abstract record PantsGcsCredentialSource
{
    PantsGcsCredentialSource()
    {
    }

    public sealed record BearerToken(string Token) : PantsGcsCredentialSource
    {
        public override string ToString() => "BearerToken { Token = [REDACTED] }";
    }

    public sealed record HmacKey(string AccessId, string Secret) : PantsGcsCredentialSource
    {
        public override string ToString() =>
            "HmacKey { AccessId = [REDACTED], Secret = [REDACTED] }";
    }

    public sealed record ApplicationDefault : PantsGcsCredentialSource;

    public sealed record ServiceAccountJsonFile(string Path) : PantsGcsCredentialSource;

    public sealed record AuthorizedUserJsonFile(string Path) : PantsGcsCredentialSource;

    public sealed record MetadataServer : PantsGcsCredentialSource;
}
