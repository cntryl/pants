namespace Pants;

internal static class S3CredentialResolver
{
    public static S3Credentials Resolve(PantsS3CredentialSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            PantsS3CredentialSource.StaticCredentials value => Create(
                value.AccessKey,
                value.SecretKey,
                value.SessionToken),
            PantsS3CredentialSource.Environment => FromEnvironment(),
            PantsS3CredentialSource.SharedProfile profile => FromProfile(profile),
            PantsS3CredentialSource.AwsDefaultChain => ResolveDefaultChain(),
            _ => throw new PantsNotSupportedException("The S3 credential source is unsupported.")
        };
    }

    private static S3Credentials ResolveDefaultChain()
    {
        try
        {
            return FromEnvironment();
        }
        catch (PantsInvalidArgumentException)
        {
            return FromProfile(new PantsS3CredentialSource.SharedProfile());
        }
    }

    private static S3Credentials FromEnvironment() => Create(
        Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ??
        Environment.GetEnvironmentVariable("AWS_ACCESS_KEY"),
        Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ??
        Environment.GetEnvironmentVariable("AWS_SECRET_KEY"),
        Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"));

    private static S3Credentials FromProfile(PantsS3CredentialSource.SharedProfile profile)
    {
        string profileName = profile.Profile ??
            Environment.GetEnvironmentVariable("AWS_PROFILE") ??
            "default";
        string credentialsPath = profile.CredentialsFile ??
            Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".aws",
                "credentials");
        if (!File.Exists(credentialsPath))
        {
            throw new PantsInvalidArgumentException(
                $"AWS shared credentials file for profile '{profileName}' does not exist.");
        }

        Dictionary<string, string> fields = ReadProfile(credentialsPath, profileName);
        return Create(
            fields.GetValueOrDefault("aws_access_key_id"),
            fields.GetValueOrDefault("aws_secret_access_key"),
            fields.GetValueOrDefault("aws_session_token"));
    }

    private static Dictionary<string, string> ReadProfile(string path, string profileName)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool active = false;
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                active = StringComparer.Ordinal.Equals(line[1..^1].Trim(), profileName);
                continue;
            }

            if (!active || line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator > 0)
            {
                fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return fields;
    }

    private static S3Credentials Create(string? accessKey, string? secretKey, string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new PantsInvalidArgumentException("AWS access-key credentials are unavailable.");
        }

        return new S3Credentials(accessKey, secretKey, sessionToken);
    }
}
