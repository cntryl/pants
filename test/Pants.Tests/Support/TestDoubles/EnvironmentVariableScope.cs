namespace Cntryl.Pants.Tests;

internal sealed class EnvironmentVariableScope : IDisposable
{
    readonly Dictionary<string, string?> _originalValues;

    public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
    {
        _originalValues = values.Keys.ToDictionary(
            static name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        foreach (var pair in values)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    public void Dispose()
    {
        foreach (var pair in _originalValues)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
