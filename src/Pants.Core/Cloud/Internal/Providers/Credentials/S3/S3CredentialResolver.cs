namespace Cntryl.Pants.Cloud.Internal.Providers.Credentials.S3;

static class S3CredentialResolver
{
    public static IS3CredentialProvider Resolve(
        PantsS3CredentialSource source,
        string region,
        bool allowAwsDefaultChain,
        HttpClient httpClient,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);
        return source switch
        {
            PantsS3CredentialSource.StaticCredentials value => new StaticS3CredentialProvider(
                Create(value.AccessKey, value.SecretKey, value.SessionToken)),
            PantsS3CredentialSource.Environment => new StaticS3CredentialProvider(
                FromEnvironment() ?? throw new PantsInvalidArgumentException(
                    "AWS-style environment credentials are unavailable.")),
            PantsS3CredentialSource.SharedProfile profile => new StaticS3CredentialProvider(
                FromProfile(profile) ?? throw new PantsInvalidArgumentException(
                    "AWS-style shared profile credentials are unavailable.")),
            PantsS3CredentialSource.AwsDefaultChain when allowAwsDefaultChain =>
                new RefreshingS3CredentialProvider(region, httpClient, timeout, timeProvider),
            PantsS3CredentialSource.AwsDefaultChain => throw new PantsInvalidArgumentException(
                "AwsDefaultChain is valid only for AWS S3; use static, environment, or shared-profile credentials for S3-compatible providers."),
            _ => throw new PantsNotSupportedException("The S3 credential source is unsupported.")
        };
    }

    internal static S3Credentials? FromEnvironment()
    {
        var accessKey = FirstNonempty("AWS_ACCESS_KEY_ID", "AWS_ACCESS_KEY");
        var secretKey = FirstNonempty("AWS_SECRET_ACCESS_KEY", "AWS_SECRET_KEY");
        if (accessKey is null || secretKey is null)
        {
            return null;
        }

        return new S3Credentials(
            accessKey,
            secretKey,
            FirstNonempty("AWS_SESSION_TOKEN", "AWS_SECURITY_TOKEN"));
    }

    internal static S3Credentials? FromDefaultProfile()
    {
        var profile = Environment.GetEnvironmentVariable("AWS_PROFILE") ?? "default";
        var credentialsPath = Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") ??
                              DefaultHomeFile(".aws/credentials");
        var configPath = Environment.GetEnvironmentVariable("AWS_CONFIG_FILE") ??
                         DefaultHomeFile(".aws/config");
        return FromProfileFiles(profile, credentialsPath, configPath);
    }

    static S3Credentials? FromProfile(PantsS3CredentialSource.SharedProfile profile)
    {
        var profileName = profile.Profile ??
                          Environment.GetEnvironmentVariable("AWS_PROFILE") ??
                          "default";
        var credentialsPath = profile.CredentialsFile ??
                              Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") ??
                              DefaultHomeFile(".aws/credentials");
        var configPath = profile.ConfigFile ??
                         Environment.GetEnvironmentVariable("AWS_CONFIG_FILE") ??
                         DefaultHomeFile(".aws/config");
        return FromProfileFiles(profileName, credentialsPath, configPath);
    }

    static S3Credentials? FromProfileFiles(
        string profile,
        string? credentialsPath,
        string? configPath)
    {
        if (credentialsPath is not null &&
            ReadProfile(credentialsPath, profile, false) is { } credentials)
        {
            return credentials;
        }

        return configPath is null
            ? null
            : ReadProfile(configPath, profile, true);
    }

    static S3Credentials? ReadProfile(string path, string profile, bool isConfigFile)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (IOException exception)
        {
            throw new PantsIOException("AWS shared profile file could not be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new PantsIOException("AWS shared profile file could not be read.", exception);
        }

        var section = isConfigFile && !StringComparer.Ordinal.Equals(profile, "default")
            ? $"profile {profile}"
            : profile;
        var fields = ReadIniSection(content, section);
        if (fields is null)
        {
            return null;
        }

        if (fields.ContainsKey("credential_process") ||
            fields.ContainsKey("sso_start_url") ||
            fields.ContainsKey("sso_session"))
        {
            throw new PantsInvalidArgumentException(
                $"AWS profile '{profile}' uses credential_process or SSO, which Pants does not execute without SDK or CLI support.");
        }

        var accessKey = fields.GetValueOrDefault("aws_access_key_id") ??
                        fields.GetValueOrDefault("access_key_id");
        var secretKey = fields.GetValueOrDefault("aws_secret_access_key") ??
                        fields.GetValueOrDefault("secret_access_key");
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            return null;
        }

        return new S3Credentials(
            accessKey,
            secretKey,
            fields.GetValueOrDefault("aws_session_token") ??
            fields.GetValueOrDefault("aws_security_token"));
    }

    static Dictionary<string, string>? ReadIniSection(string content, string section)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var active = false;
        var found = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                active = StringComparer.Ordinal.Equals(line[1..^1].Trim(), section);
                found |= active;
                continue;
            }

            if (!active || line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return found ? fields : null;
    }

    static S3Credentials Create(string? accessKey, string? secretKey, string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw new PantsInvalidArgumentException("AWS access-key credentials are unavailable.");
        }

        return new S3Credentials(
            accessKey,
            secretKey,
            string.IsNullOrWhiteSpace(sessionToken) ? null : sessionToken);
    }

    static string? DefaultHomeFile(string relativePath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, relativePath);
    }

    static string? FirstNonempty(params string[] names) => names
        .Select(Environment.GetEnvironmentVariable)
        .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}
