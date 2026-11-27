using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PrintingHouse.Core;
using Xunit;

namespace PrintingHouse.Tests;

public sealed class PrintFilesTests
{
    [Theory]
    [InlineData(',')]
    [InlineData(';')]
    public void ReadsQuotedCsvAndUtf8Bom(char separator)
    {
        string input = $"\uFEFFName{separator}Phone{separator}Email\r\n\"Печать, бумага; студия\"{separator}+79990001234{separator}test@example.com";
        var customer = Assert.Single(PrintFiles.ReadCustomers(input));
        Assert.Equal("Печать, бумага; студия", customer.Name);
    }

    [Theory]
    [InlineData("Wrong,Phone,Email\nПлакат,123,")]
    [InlineData("Name,Phone,Email\nПлакат,,")]
    [InlineData("Name,Phone,Email\nПлакат,123,bad-email")]
    [InlineData("Name,Phone,Email\n\"незакрытая кавычка,123,")]
    [InlineData("Name,Phone,Email")]
    public void RejectsInvalidCsv(string input) =>
        Assert.ThrowsAny<RuleException>(() => PrintFiles.ReadCustomers(input));

    [Fact]
    public void ExportsThreeReadableFormats()
    {
        var row = OrderRulesTests.Row(OrderStatus.Layout, 4200.5m, DateTime.UnixEpoch, null);
        Assert.Equal(row, Assert.Single(JsonSerializer.Deserialize<OrderInfo[]>(
            PrintFiles.Export("json", [row]).Bytes, PrintFiles.JsonOptions)!));
        var xml = XDocument.Parse(Encoding.UTF8.GetString(PrintFiles.Export("xml", [row]).Bytes));
        Assert.Equal(row.Title, xml.Root!.Element("Order")!.Element("Title")!.Value);
        var csv = Encoding.UTF8.GetString(PrintFiles.Export("csv", [row]).Bytes);
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"Каталог \"\"Город\"\" <тираж>\"", csv);
        Assert.Contains("\"'+79990001234\"", csv);
        Assert.Contains("\"4200.5\"", csv);
    }
}
