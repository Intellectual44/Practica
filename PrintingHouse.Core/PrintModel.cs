using System.Diagnostics;

namespace PrintingHouse.Core;

public enum OrderStatus { Accepted, Layout, Approved, Printing, Ready, Issued, Cancelled }
public enum UserRole { Admin, Manager, Printer }

public static class Roles
{
    public const string Admin = nameof(UserRole.Admin);
    public const string Manager = nameof(UserRole.Manager);
    public const string Printer = nameof(UserRole.Printer);
    public const string Staff = Admin + "," + Manager;
}

[DebuggerDisplay("Заказчик #{Id}")]
public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime? RegisteredAt { get; set; }
}

[DebuggerDisplay("Пользователь #{Id} ({Role,nq})")]
public sealed class PrintUser
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsDisabled { get; set; }
}

[DebuggerDisplay("Заказ #{Id} · {Status} · версия {Version}")]
public sealed class PrintOrder
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Title { get; set; } = "";
    public string Specification { get; set; } = "";
    public int Quantity { get; set; }
    public string? LayoutReference { get; set; }
    public string Notes { get; set; } = "";
    public int? PrinterId { get; set; }
    public PrintUser? Printer { get; set; }
    public OrderStatus Status { get; set; }
    public decimal? Amount { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Version { get; set; }
}

public sealed record CustomerInput(string Name, string Phone, string? Email);
public sealed record CustomerInfo(int Id, string Name, string Phone, string Email);
public sealed record UserInfo(int Id, string Login, string DisplayName, string Role, bool IsDisabled = false);
public sealed record UserInput(string Login, string DisplayName, string Role, string Password);
public sealed record UserAccessInput(bool IsDisabled);
public sealed record LoginInput(string Login, string Password);
public sealed record LoginResult(string Token, UserInfo User);
public sealed record OrderInput(int CustomerId, string Title, string Specification, string? Notes,
    int? PrinterId, decimal? Amount, DateOnly? DueDate, int? Version = null, int Quantity = 0, string? LayoutReference = null);
public sealed record TransitionInput(OrderStatus Status, string? Reason, int Version);
public sealed record OrderInfo(int Id, int CustomerId, string Customer, string Phone, string Title,
    string Specification, string Notes, int? PrinterId, string? Printer, OrderStatus Status,
    decimal? Amount, DateOnly? DueDate, string? CancellationReason, DateTime CreatedAt,
    DateTime? CompletedAt, int Version, int Quantity, string? LayoutReference);
public sealed record StatusCount(OrderStatus Status, int Count);
public readonly record struct OrderMetrics(OrderStatus Status, decimal? Amount, DateTime CreatedAt, DateTime? CompletedAt);
public sealed record PrintStatistics(int Total, int Open, int Issued, int Cancelled,
    decimal TotalAmount, decimal IssuedAmount, double? AverageDays, StatusCount[] ByStatus);
public sealed record ImportResult(int Imported);
public sealed record HealthInfo(string Status);

public sealed class OrderAccessException(string message) : Exception(message);
public sealed class UserConflictException(string message) : Exception(message);

public class RuleException(string message) : Exception(message);
public sealed class OrderConflictException() : Exception("Обновите список, откройте заказ и повторите действие.");
public sealed record CsvRowError(long Line, string Message);
public sealed class CsvImportException(IReadOnlyList<CsvRowError> errors) :
    RuleException(string.Join(Environment.NewLine, errors.Take(10).Select(row => $"Строка {row.Line}: {row.Message}"))
        + (errors.Count > 10 ? $"\nВсего ошибок: {errors.Count}." : ""))
{
    public IReadOnlyList<CsvRowError> Errors { get; } = errors;
}
