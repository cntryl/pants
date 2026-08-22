namespace Pants;

internal sealed record CloudObjectListPage
{
    public CloudObjectListPage(
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
