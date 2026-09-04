namespace Cntryl.Pants.Support.TestDoubles;

sealed record RecordedCredentialRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}
