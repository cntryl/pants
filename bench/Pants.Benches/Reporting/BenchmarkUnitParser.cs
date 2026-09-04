using System.Globalization;

namespace Cntryl.Pants.Reporting;

static class BenchmarkUnitParser
{
    public static double ParseTimeNanoseconds(string value) => Parse(
        value,
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["ns"] = 1,
            ["us"] = 1_000,
            ["μs"] = 1_000,
            ["ms"] = 1_000_000,
            ["s"] = 1_000_000_000
        },
        "time");

    public static double ParseBytes(string value) => Parse(
        value,
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = 1,
            ["KB"] = 1_024,
            ["MB"] = 1_024 * 1_024,
            ["GB"] = 1_024 * 1_024 * 1_024
        },
        "allocation");

    static double Parse(string value, Dictionary<string, double> units, string kind)
    {
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var amount) ||
            !units.TryGetValue(parts[1], out var multiplier) ||
            !double.IsFinite(amount) || amount < 0)
        {
            throw new InvalidDataException($"Invalid benchmark {kind} value '{value}'.");
        }

        return amount * multiplier;
    }
}
