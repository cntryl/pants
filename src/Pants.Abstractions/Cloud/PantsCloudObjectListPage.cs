namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudObjectListPage
{
    public PantsCloudObjectListPage(
        IEnumerable<string> objectKeys,
        string? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(objectKeys);
        ObjectKeys = Array.AsReadOnly(objectKeys.ToArray());
        ContinuationToken = continuationToken;
    }

    public IReadOnlyList<string> ObjectKeys { get; }

    public string? ContinuationToken { get; }
}
