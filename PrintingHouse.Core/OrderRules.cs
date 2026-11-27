using System.Diagnostics;
using System.Globalization;

namespace PrintingHouse.Core;

public static class OrderRules
{
    public static bool IsFinal(OrderStatus status) =>
        status is OrderStatus.Issued or OrderStatus.Cancelled;

    public static IReadOnlyList<OrderStatus> Next(OrderStatus status) => status switch
    {
        OrderStatus.Accepted => [OrderStatus.Layout, OrderStatus.Cancelled],
        OrderStatus.Layout => [OrderStatus.Approved, OrderStatus.Cancelled],
        OrderStatus.Approved => [OrderStatus.Printing, OrderStatus.Cancelled],
        OrderStatus.Printing => [OrderStatus.Ready, OrderStatus.Cancelled],
        OrderStatus.Ready => [OrderStatus.Issued, OrderStatus.Cancelled],
        _ => []
    };

    public static IReadOnlyList<OrderStatus> Available(OrderStatus status, string role) =>
        role == Roles.Printer
            ? status == OrderStatus.Printing ? [OrderStatus.Ready] : []
            : Next(status);

    public static void Transition(PrintOrder order, OrderStatus target, string? reason, DateTime now)
    {
        if (!Next(order.Status).Contains(target))
            throw new RuleException("Переход из текущего статуса недоступен.");

        if (target == OrderStatus.Approved && string.IsNullOrWhiteSpace(order.LayoutReference))
            throw new RuleException("Перед согласованием укажите ссылку или имя файла макета.");

        if (target == OrderStatus.Printing && order.PrinterId is null)
            throw new RuleException("Перед печатью назначьте печатника.");

        if (target == OrderStatus.Issued && (order.Amount is not > 0 || order.DueDate is null))
            throw new RuleException("Для выдачи укажите положительную сумму и срок выдачи.");

        if (target == OrderStatus.Cancelled)
            order.CancellationReason = InputRules.Required(reason, "Причина отмены", 500);

        if (target == OrderStatus.Issued)
            order.CompletedAt = now;
        order.Status = target;
        order.Version++;
        Debug.Assert(order.Status != OrderStatus.Issued || order.CompletedAt.HasValue);
        Debug.Assert(order.Status != OrderStatus.Cancelled || !string.IsNullOrWhiteSpace(order.CancellationReason));
    }

    public static PrintStatistics Statistics(IReadOnlyList<OrderInfo> rows) =>
        StatisticsFromMetrics(rows.Select(row => new OrderMetrics(row.Status, row.Amount, row.CreatedAt, row.CompletedAt)));

    public static PrintStatistics StatisticsFromMetrics(IEnumerable<OrderMetrics> rows)
    {
        var statuses = Enum.GetValues<OrderStatus>();
        var counts = new int[statuses.Length];
        int total = 0, durationCount = 0;
        decimal totalAmount = 0, installedAmount = 0;
        double duration = 0;
        foreach (var row in rows)
        {
            total++;
            if ((uint)row.Status < counts.Length)
                counts[(int)row.Status]++;
            totalAmount += row.Amount ?? 0;
            if (row.Status != OrderStatus.Issued)
                continue;
            installedAmount += row.Amount ?? 0;
            if (row.CompletedAt is { } completed && completed >= row.CreatedAt)
            {
                duration += (completed - row.CreatedAt).TotalDays;
                durationCount++;
            }
        }
        int issued = counts[(int)OrderStatus.Issued], cancelled = counts[(int)OrderStatus.Cancelled];
        return new PrintStatistics(total, total - issued - cancelled, issued, cancelled, totalAmount,
            installedAmount, durationCount == 0 ? null : Math.Round(duration / durationCount, 2),
            statuses.Select(status => new StatusCount(status, counts[(int)status])).ToArray());
    }
}

public static class InputRules
{
    public static int ParseQuantity(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            throw new RuleException("Введите тираж целым числом.");
        CheckQuantity(value);
        return value;
    }

    public static void CheckQuantity(int quantity)
    {
        if (quantity is < 1 or > 1_000_000)
            throw new RuleException("Тираж должен быть от 1 до 1 000 000 экземпляров.");
    }

    public static UserInput User(UserInput input)
    {
        var login = Required(input.Login, "Логин", 80).ToLowerInvariant();
        if (login.Length < 3 || login.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new RuleException("Логин должен содержать от 3 латинских букв, цифр или знаков . _ -.");
        if (input.Role is not (Roles.Admin or Roles.Manager or Roles.Printer))
            throw new RuleException("Выберите роль сотрудника.");
        if (input.Password is null || input.Password.Length is < 10 or > 128 || !input.Password.Any(char.IsDigit) ||
            !input.Password.Any(char.IsLetter) || input.Password.Any(char.IsWhiteSpace))
            throw new RuleException("Пароль должен содержать от 10 до 128 символов, букву, цифру и не содержать пробелов.");
        return input with { Login = login, DisplayName = Required(input.DisplayName, "Имя сотрудника", 120) };
    }

    public static decimal? ParseAmount(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return null;
        int separator = text.IndexOfAny([',', '.']);
        if ((separator >= 0 && text.Length - separator - 1 is < 1 or > 2) ||
            !decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out var amount))
            throw new RuleException("Введите сумму без разделителей тысяч, максимум с двумя знаками после запятой.");
        CheckAmount(amount);
        return amount;
    }

    public static void CheckAmount(decimal? amount)
    {
        if (amount is <= 0 or > 999_999_999 || amount is { } value && value != decimal.Round(value, 2))
            throw new RuleException("Сумма должна быть положительной, до 999 999 999 ₽ и максимум с двумя знаками после запятой.");
    }

    public static DateOnly? ParseDueDate(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return null;
        if (DateOnly.TryParseExact(text, ["dd.MM.yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var date))
            return date;
        throw new RuleException("Введите срок выдачи в формате ДД.ММ.ГГГГ, например 23.11.2026.");
    }

    public static string Required(string? value, string field, int max)
    {
        var trimmed = Optional(value, field, max);
        if (trimmed.Length == 0)
            throw new RuleException($"Заполните поле «{field}».");
        return trimmed;
    }

    public static string Optional(string? value, string field, int max)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length > max)
            throw new RuleException($"Поле «{field}» допускает до {max} символов.");
        return trimmed;
    }

    public static CustomerInput Customer(CustomerInput input)
    {
        var name = Required(input.Name, "Имя заказчика", 150);
        var phone = Required(input.Phone, "Телефон", 40);
        var email = Optional(input.Email, "Эл. почта", 150);
        if (email.Length != 0 && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
            throw new RuleException("Введите корректный адрес электронной почты.");
        return new CustomerInput(name, phone, email);
    }
}
