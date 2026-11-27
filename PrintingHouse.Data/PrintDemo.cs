using PrintingHouse.Core;
using Microsoft.EntityFrameworkCore;

namespace PrintingHouse.Data;

internal static class PrintDemo
{
    public static async Task Seed(DbContext db, CancellationToken ct)
    {
        if (await db.Set<Customer>().AnyAsync(ct) || await db.Set<PrintOrder>().AnyAsync(ct))
            return;

        var start = DateTime.UtcNow.Date.AddDays(-10);
        Customer[] customers =
        [
            new() { Name = "Издательская студия «Переплёт»", Phone = "+7 000 012-01-01", Email = "books@example.test", RegisteredAt = start },
            new() { Name = "Кофейня «Зерно»", Phone = "+7 000 012-01-02", Email = "coffee@example.test", RegisteredAt = start },
            new() { Name = "Музей «Городская история»", Phone = "+7 000 012-01-03", Email = "museum@example.test", RegisteredAt = start },
            new() { Name = "Цветочная студия «Ирис»", Phone = "+7 000 012-01-04", Email = "iris@example.test", RegisteredAt = start }
        ];

        db.AddRange(
            Order(0, "Каталог выставки", "А5 · 32 полосы · 4+4", 450, OrderStatus.Accepted, customer: 2),
            Order(1, "Меню для кофейни", "А4 · матовая ламинация · 4+4", 120, OrderStatus.Layout, customer: 1, layout: "menu-v2.pdf", price: 9_600),
            Order(2, "Этикетки для букетов", "70 × 45 мм · самоклейка · 4+0", 900, OrderStatus.Approved, customer: 3, layout: "iris-labels.pdf", price: 10_800),
            Order(3, "Брошюра «Городские маршруты»", "А5 · 24 полосы · скрепка", 600, OrderStatus.Printing, customer: 2, printer: 3, layout: "routes-final.pdf", price: 32_400),
            Order(4, "Открытки «Старый город»", "А6 · картон 300 г/м² · 4+4", 350, OrderStatus.Ready, customer: 2, printer: 4, layout: "city-cards.pdf", price: 12_250),
            Order(5, "Закладки для серии книг", "60 × 180 мм · бумага 250 г/м²", 1000, OrderStatus.Issued, customer: 0, printer: 3, layout: "bookmarks.pdf", price: 19_000),
            Order(6, "Афиши музейной программы", "А3 · бумага 170 г/м² · 4+0", 75, OrderStatus.Cancelled, customer: 2),
            Order(7, "Визитки для студии", "90 × 50 мм · картон 350 г/м²", 500, OrderStatus.Printing, customer: 3, printer: 4, layout: "iris-cards.pdf", price: 8_750),
            Order(8, "Обложки для блокнотов", "А5 · тиснение · 4+0", 200, OrderStatus.Issued, customer: 0, printer: 4, layout: "cover.pdf", price: 16_200));
        await db.SaveChangesAsync(ct);

        PrintOrder Order(int index, string title, string specification, int quantity, OrderStatus status,
            int customer, int? printer = null, string? layout = null, decimal? price = null) => new()
        {
            Customer = customers[customer],
            Title = title,
            Specification = specification,
            Quantity = quantity,
            LayoutReference = layout,
            Notes = status == OrderStatus.Printing ? "Перед сдачей проверить цвет на контрольном оттиске." : "",
            Status = status,
            PrinterId = printer,
            Amount = price,
            DueDate = status == OrderStatus.Cancelled ? null : DateOnly.FromDateTime(start.AddDays(index + 3)),
            CreatedAt = start.AddDays(index).AddHours(9),
            CompletedAt = status == OrderStatus.Issued ? start.AddDays(index + 2).AddHours(12) : null,
            CancellationReason = status == OrderStatus.Cancelled ? "Музей перенёс мероприятие" : null,
            Version = 1
        };
    }
}
