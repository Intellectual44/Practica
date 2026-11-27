using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;

namespace PrintingHouse.Core;

public static class PrintFiles
{
    public const int MaxImportBytes = 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static IReadOnlyList<CustomerInput> ReadCustomers(string csv)
    {
        csv = csv.TrimStart('\uFEFF');
        using var parser = new TextFieldParser(new StringReader(csv));
        var physicalLines = csv.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var headerLine = physicalLines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        parser.SetDelimiters(headerLine?.Contains(';') == true ? ";" : ",");
        var customers = new List<CustomerInput>();
        var errors = new List<CsvRowError>();
        try
        {
            var header = parser.ReadFields();
            if (header is null || !header.SequenceEqual(new[] { "Name", "Phone", "Email" }, StringComparer.OrdinalIgnoreCase))
                throw new RuleException("Заголовок CSV должен содержать Name,Phone,Email.");
        }
        catch (MalformedLineException error)
        {
            throw new CsvImportException([new CsvRowError(error.LineNumber, "Некорректный заголовок CSV.")]);
        }

        int records = 0;
        while (!parser.EndOfData)
        {
            if (++records > 5000)
                throw new RuleException("Один импорт допускает до 5000 заказчиков.");

            long line = parser.LineNumber;
            while (line > 0 && line <= physicalLines.Length && string.IsNullOrWhiteSpace(physicalLines[line - 1]))
                line++;
            try
            {
                var fields = parser.ReadFields()!;
                if (fields.Length != 3)
                    throw new RuleException("Ожидаются три поля.");
                customers.Add(InputRules.Customer(new CustomerInput(fields[0], fields[1], fields[2])));
            }
            catch (RuleException error)
            {
                errors.Add(new CsvRowError(line, error.Message));
            }
            catch (MalformedLineException error)
            {
                errors.Add(new CsvRowError(error.LineNumber, "Некорректный CSV."));
            }
        }
        if (errors.Count != 0)
            throw new CsvImportException(errors);
        if (customers.Count == 0)
            throw new RuleException("В CSV отсутствуют заказчики.");
        return customers;
    }

    public static (byte[] Bytes, string ContentType) Export(string format, IReadOnlyList<OrderInfo> orders)
    {
        if (format == "json")
            return (JsonSerializer.SerializeToUtf8Bytes(orders, JsonOptions), "application/json");

        string[] columns = ["Id", "CustomerId", "Customer", "Phone", "Title", "Specification", "Notes",
            "PrinterId", "Printer", "Status", "Amount", "DueDate", "CancellationReason",
            "CreatedAt", "CompletedAt", "Version", "Quantity", "LayoutReference"];
        var rows = orders.Select(Row);

        if (format == "xml")
        {
            var root = new XElement("Orders", rows.Select(row =>
                new XElement("Order", columns.Zip(row, (column, value) => new XElement(column, value)))));
            return (Encoding.UTF8.GetBytes(root.ToString()), "application/xml");
        }

        if (format != "csv")
            throw new RuleException("Доступные форматы выгрузки: CSV, JSON и XML.");

        var csv = new StringBuilder("\uFEFF").AppendJoin(';', columns).Append("\r\n");
        foreach (var row in rows)
            csv.AppendJoin(';', row.Select((value, index) => ForCsv(value, index))).Append("\r\n");
        return (Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8");
    }

    private static string?[] Row(OrderInfo order) =>
    [
        Invariant(order.Id), Invariant(order.CustomerId), order.Customer, order.Phone,
        order.Title, order.Specification, order.Notes, Invariant(order.PrinterId), order.Printer,
        order.Status.ToString(), Invariant(order.Amount), order.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        order.CancellationReason, order.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        order.CompletedAt?.ToString("O", CultureInfo.InvariantCulture), Invariant(order.Version),
        Invariant(order.Quantity), order.LayoutReference
    ];

    private static string? Invariant(IFormattable? value) => value?.ToString(null, CultureInfo.InvariantCulture);

    private static string ForCsv(string? value, int index)
    {
        value ??= "";
        if (index is 2 or 3 or 4 or 5 or 6 or 8 or 12 or 17 &&
            value.TrimStart() is ['=' or '+' or '-' or '@', ..])
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
