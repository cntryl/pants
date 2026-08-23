using Microsoft.VisualBasic.FileIO;

namespace Cntryl.Pants.Benches.Reporting;

static class CsvTable
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string content)
    {
        using var reader = new StringReader(content);
        using var parser = new TextFieldParser(reader)
        {
            HasFieldsEnclosedInQuotes = true,
            TextFieldType = FieldType.Delimited,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

        string[] headers;
        try
        {
            headers = parser.ReadFields() ?? throw new InvalidDataException("The benchmark CSV is empty.");
        }
        catch (MalformedLineException exception)
        {
            throw new InvalidDataException("The benchmark CSV is malformed.", exception);
        }

        if (headers.Length == 0 || headers.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("The benchmark CSV has an invalid header.");
        }

        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
        {
            throw new InvalidDataException("The benchmark CSV has duplicate headers.");
        }

        var records = new List<IReadOnlyDictionary<string, string>>();
        while (!parser.EndOfData)
        {
            string[] record;
            try
            {
                record = parser.ReadFields() ?? [];
            }
            catch (MalformedLineException exception)
            {
                throw new InvalidDataException($"CSV row {parser.LineNumber} is malformed.", exception);
            }

            if (record.Length != headers.Length)
            {
                throw new InvalidDataException(
                    $"CSV row {records.Count + 2} has {record.Length} fields; expected {headers.Length}.");
            }

            records.Add(headers.Zip(record)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal));
        }

        return records;
    }
}
