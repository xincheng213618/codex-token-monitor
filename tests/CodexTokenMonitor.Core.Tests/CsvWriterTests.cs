using Xunit;

namespace CodexTokenMonitor.Tests;

public sealed class CsvWriterTests
{
    [Fact]
    public void Build_EscapesCommaQuotesAndNewlines()
    {
        var csv = CsvWriter.Build(new[]
        {
            new string?[] { "name", "value" },
            new string?[] { "a,b", "say \"hi\"\nnext", null }
        });

        var expected =
            "name,value" + Environment.NewLine +
            "\"a,b\",\"say \"\"hi\"\"\nnext\"," + Environment.NewLine;

        Assert.Equal(expected, csv);
    }

    [Fact]
    public void Build_EmptyRowsProducesEmptyText()
    {
        Assert.Equal(string.Empty, CsvWriter.Build(Array.Empty<IEnumerable<string?>>()));
    }
}
