using System.Linq.Expressions;
using PrintingHouse.Core;
using Microsoft.EntityFrameworkCore;

namespace PrintingHouse.Data;

public sealed class PrintStore(PrintDb db, TimeProvider time) : IOrderRepository, ICustomerRepository, IUserRepository
{
    private static readonly Expression<Func<PrintOrder, OrderInfo>> OrderProjection = row =>
        new OrderInfo(row.Id, row.CustomerId, row.Customer.Name, row.Customer.Phone, row.Title,
            row.Specification, row.Notes, row.PrinterId, row.Printer == null ? null : row.Printer.DisplayName,
            row.Status, row.Amount, row.DueDate, row.CancellationReason, row.CreatedAt,
            row.CompletedAt, row.Version, row.Quantity, row.LayoutReference);

    public Task<List<OrderInfo>> FindOrders(UserInfo user, OrderStatus? status, string? search, CancellationToken ct)
    {
        var query = VisibleOrders(user).AsNoTracking();
        if (status.HasValue)
            query = query.Where(row => row.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            bool exactId = term[0] == '#' && int.TryParse(term.AsSpan(1), out _);
            int.TryParse(term.TrimStart('#'), out int orderId);
            // SQLite's built-in NOCASE collation does not fold Cyrillic.
            query = exactId
                ? query.Where(row => row.Id == orderId)
                : query.Where(row => (orderId > 0 && row.Id == orderId) ||
                    PrintContains(row.Title, term) || PrintContains(row.Specification, term) ||
                    PrintContains(row.LayoutReference ?? "", term) ||
                    PrintContains(row.Customer.Name, term) || PrintContains(row.Customer.Phone, term));
        }

        return query.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .Select(OrderProjection).ToListAsync(ct);
    }

    [DbFunction("print_contains", IsBuiltIn = true)]
    public static bool PrintContains(string value, string term) =>
        throw new InvalidOperationException("Функция выполняется в SQLite.");

    public Task<OrderInfo?> FindOrder(int id, UserInfo user, CancellationToken ct) =>
        VisibleOrders(user).AsNoTracking().Where(row => row.Id == id)
            .Select(OrderProjection).SingleOrDefaultAsync(ct);

    public Task<List<OrderMetrics>> FindMetrics(UserInfo user, CancellationToken ct) =>
        VisibleOrders(user).AsNoTracking()
            .Select(row => new OrderMetrics(row.Status, row.Amount, row.CreatedAt, row.CompletedAt)).ToListAsync(ct);

    public Task<List<CustomerInfo>> GetCustomers(CancellationToken ct) => db.Customers.AsNoTracking()
        .OrderBy(customer => customer.Name).ThenBy(customer => customer.Id)
        .Select(customer => new CustomerInfo(customer.Id, customer.Name, customer.Phone, customer.Email)).ToListAsync(ct);

    public Task<List<UserInfo>> GetUsers(bool printersOnly, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(user => !printersOnly || (user.Role == Roles.Printer && !user.IsDisabled))
            .OrderBy(user => user.Id)
            .Select(user => new UserInfo(user.Id, user.Login, user.DisplayName, user.Role, user.IsDisabled)).ToListAsync(ct);

    public Task<PrintUser?> FindUser(string login, CancellationToken ct) =>
        db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Login == login, ct);

    public async Task<int> CreateUser(UserInput input, CancellationToken ct)
    {
        var values = InputRules.User(input);
        string salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var user = new PrintUser
        {
            Login = values.Login, DisplayName = values.DisplayName, Role = values.Role,
            PasswordHash = salt + ":" + PrintDatabase.Hash(values.Password, salt)
        };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.Sqlite.SqliteException
            { SqliteExtendedErrorCode: 2067 })
        {
            throw new UserConflictException("Этот логин уже занят.");
        }
        return user.Id;
    }

    public async Task<bool> SetUserAccess(int id, int actorId, bool isDisabled, CancellationToken ct)
    {
        if (id == actorId)
            throw new RuleException("Доступ к своей учётной записи изменить нельзя.");
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
            return false;
        user.IsDisabled = isDisabled;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> CreateCustomer(CustomerInput input, CancellationToken ct)
    {
        var customer = NewCustomer(InputRules.Customer(input));
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer.Id;
    }

    public async Task<bool> UpdateCustomer(int id, CustomerInput input, CancellationToken ct)
    {
        var customer = await db.Customers.FindAsync([id], ct);
        if (customer is null)
            return false;

        var values = InputRules.Customer(input);
        customer.Name = values.Name;
        customer.Phone = values.Phone;
        customer.Email = values.Email!;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> ImportCustomers(string csv, CancellationToken ct)
    {
        var values = PrintFiles.ReadCustomers(csv);
        db.Customers.AddRange(values.Select(NewCustomer));
        await db.SaveChangesAsync(ct);
        return values.Count;
    }

    public async Task<int> CreateOrder(OrderInput input, CancellationToken ct)
    {
        var order = new PrintOrder { CreatedAt = time.GetUtcNow().UtcDateTime, Version = 1 };
        await SetDetails(order, input, ct);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return order.Id;
    }

    public async Task<bool> UpdateOrder(int id, OrderInput input, CancellationToken ct)
    {
        var order = await db.Orders.FindAsync([id], ct);
        if (order is null)
            return false;

        CheckVersion(order, input.Version);
        if (OrderRules.IsFinal(order.Status))
            throw new RuleException("Завершённый заказ закрыт для редактирования.");

        await SetDetails(order, input, ct);
        order.Version++;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> Transition(int id, UserInfo user, TransitionInput input, CancellationToken ct)
    {
        var order = await VisibleOrders(user).SingleOrDefaultAsync(row => row.Id == id, ct);
        if (order is null)
            return false;

        if (!OrderRules.Available(order.Status, user.Role).Contains(input.Status) && user.Role == Roles.Printer)
            throw new OrderAccessException("Печатник может отметить готовность своего тиража.");
        CheckVersion(order, input.Version);
        if (order.Status == OrderStatus.Approved && input.Status == OrderStatus.Printing &&
            order.PrinterId is { } printerId &&
            !await db.Users.AnyAsync(row => row.Id == printerId && !row.IsDisabled, ct))
            throw new RuleException("Назначьте действующего печатника.");
        OrderRules.Transition(order, input.Status, input.Reason, time.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private IQueryable<PrintOrder> VisibleOrders(UserInfo user) =>
        user.Role == Roles.Printer ? db.Orders.Where(row => row.PrinterId == user.Id) : db.Orders;

    private static void CheckVersion(PrintOrder order, int? version)
    {
        if (version is null)
            throw new RuleException("Укажите версию заказа.");
        if (order.Version != version)
            throw new OrderConflictException();
    }

    private Customer NewCustomer(CustomerInput input) =>
        new() { Name = input.Name, Phone = input.Phone, Email = input.Email!, RegisteredAt = time.GetUtcNow().UtcDateTime };

    private async Task SetDetails(PrintOrder order, OrderInput input, CancellationToken ct)
    {
        order.Title = InputRules.Required(input.Title, "Продукция", 180);
        order.Specification = InputRules.Required(input.Specification, "Параметры печати", 220);
        order.Notes = InputRules.Optional(input.Notes, "Примечания", 2000);
        InputRules.CheckAmount(input.Amount);
        InputRules.CheckQuantity(input.Quantity);
        order.Quantity = input.Quantity;
        var layoutReference = InputRules.Optional(input.LayoutReference, "Макет", 300);
        if ((order.Status is OrderStatus.Approved or OrderStatus.Printing or OrderStatus.Ready) && layoutReference.Length == 0)
            throw new RuleException("Сохраните ссылку или имя согласованного макета.");
        order.LayoutReference = layoutReference.Length == 0 ? null : layoutReference;

        if (!await db.Customers.AnyAsync(customer => customer.Id == input.CustomerId, ct))
            throw new RuleException("Выберите существующего заказчика.");

        if (input.PrinterId is { } printerId &&
            !await db.Users.AnyAsync(user => user.Id == printerId && user.Role == Roles.Printer && !user.IsDisabled, ct))
            throw new RuleException("Выберите пользователя с ролью печатника.");

        if (order.Status is OrderStatus.Printing or OrderStatus.Ready && input.PrinterId is null)
            throw new RuleException("У заказа в печати или готового тиража должен быть печатник.");

        order.CustomerId = input.CustomerId;
        order.PrinterId = input.PrinterId;
        order.Amount = input.Amount;
        order.DueDate = input.DueDate;
    }
}
