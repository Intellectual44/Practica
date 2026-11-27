namespace PrintingHouse.Core;

public static class PrintText
{
    public static string Status(OrderStatus value) => value switch
    {
        OrderStatus.Accepted => "Принят",
        OrderStatus.Layout => "Макет",
        OrderStatus.Approved => "Согласован",
        OrderStatus.Printing => "Печать",
        OrderStatus.Ready => "Готов",
        OrderStatus.Issued => "Выдан",
        OrderStatus.Cancelled => "Отменён",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string Role(string role) => role switch
    {
        Roles.Admin => "Администратор",
        Roles.Manager => "Менеджер",
        Roles.Printer => "Печатник",
        _ => role
    };
}
