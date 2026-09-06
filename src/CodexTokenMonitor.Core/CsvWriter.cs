using System.Text;

namespace CodexTokenMonitor;

internal static class CsvWriter
{
    public static string Build(IEnumerable<IEnumerable<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            var first = true;
            foreach (var value in row)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(Escape(value));
                first = false;
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var requiresQuotes = value.Contains(',') ||
                             value.Contains('"') ||
                             value.Contains('\r') ||
                             value.Contains('\n');
        return requiresQuotes
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
