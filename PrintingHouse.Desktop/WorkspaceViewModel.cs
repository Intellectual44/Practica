using System.IO;
using System.Diagnostics;
using System.Windows.Input;
using PrintingHouse.Core;

namespace PrintingHouse.Desktop;

internal sealed record StatusOption(OrderStatus? Status, string Name);
internal sealed record StatusSummary(string Name, int Count);
internal sealed record OrderRow(OrderInfo Order)
{
    public string Status => PrintText.Status(Order.Status);
    public string Amount => Order.Amount is { } amount ? $"{amount:N2} ₽" : "—";
    public string Due => Order.DueDate?.ToString("dd.MM.yyyy") ?? "—";
}
internal sealed record UserRow(UserInfo User)
{
    public string RoleName => PrintText.Role(User.Role);
    public string Access => User.IsDisabled ? "Закрыт" : "Разрешён";
}

internal sealed class WorkspaceViewModel : AsyncViewModel
{
    private readonly PrintClient api;
    private readonly PrintDialogs dialogs;
    private OrderRow[] orders = [];
    private CustomerInfo[] customers = [];
    private UserRow[] users = [];
    private PrintStatistics? statistics;
    private OrderRow? selectedOrder;
    private CustomerInfo? selectedCustomer;
    private int tab;
    private readonly UserInfo actor;
    private UserRow? selectedUser;
    private CancellationTokenSource? orderLoad;
    private string search = "";
    private StatusOption? selectedFilter;

    public string SessionCaption { get; }
    public bool IsStaff { get; }
    public bool IsAdmin { get; }
    public string OrderHeading => IsStaff ? "Заказы типографии" : "Мои заказы";
    public string EmptyOrders => Orders.Length == 0 ? "Заказы по заданным условиям отсутствуют." : "";
    public string OrderCount => $"Заказов в списке: {Orders.Length}";
    public string AverageDays => Statistics?.AverageDays is { } days ? $"{days:N2} дн." : "—";
    public StatusSummary[] Statuses => Statistics?.ByStatus.Select(row =>
        new StatusSummary(PrintText.Status(row.Status), row.Count)).ToArray() ?? [];

    public StatusOption[] Filters { get; } = [new(null, "Все статусы"), .. Enum.GetValues<OrderStatus>()
        .Select(status => new StatusOption(status, PrintText.Status(status)))];
    public StatusOption? SelectedFilter
    {
        get => selectedFilter;
        set
        {
            if (selectedFilter == value)
                return;
            Set(ref selectedFilter, value);
            if (value is not null)
                RefreshFilteredOrders(TimeSpan.Zero);
        }
    }

    public string Search
    {
        get => search;
        set
        {
            if (search == value)
                return;
            Set(ref search, value);
            RefreshFilteredOrders(TimeSpan.FromMilliseconds(250));
        }
    }

    public UserRow? SelectedUser
    {
        get => selectedUser;
        set
        {
            Set(ref selectedUser, value);
            Changed(nameof(UserAccessAction));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string UserAccessAction => SelectedUser?.User.IsDisabled == true ? "Разрешить вход" : "Закрыть вход";
    public OrderRow[] Orders { get => orders; private set => Set(ref orders, value); }
    public CustomerInfo[] Customers { get => customers; private set => Set(ref customers, value); }
    public UserRow[] Users { get => users; private set => Set(ref users, value); }
    public PrintStatistics? Statistics { get => statistics; private set => Set(ref statistics, value); }
    public OrderRow? SelectedOrder { get => selectedOrder; set => Set(ref selectedOrder, value); }
    public CustomerInfo? SelectedCustomer { get => selectedCustomer; set => Set(ref selectedCustomer, value); }
    public int Tab
    {
        get => tab;
        set
        {
            if (tab == value)
                return;
            Set(ref tab, value);
            var command = value switch
            {
                1 when IsStaff => RefreshCustomers,
                2 => RefreshStatistics,
                3 when IsAdmin => RefreshUsers,
                _ => null
            };
            command?.Execute(null);
        }
    }

    public ICommand RefreshOrders { get; }
    public ICommand RefreshCustomers { get; }
    public ICommand RefreshStatistics { get; }
    public ICommand RefreshUsers { get; }
    public ICommand NewOrder { get; }
    public ICommand EditOrder { get; }
    public ICommand Details { get; }
    public ICommand Transition { get; }
    public ICommand NewCustomer { get; }
    public ICommand EditCustomer { get; }
    public ICommand Import { get; }
    public ICommand Export { get; }
    public ICommand Logout { get; }
    public ICommand NewUser { get; }
    public ICommand ToggleUserAccess { get; }

    public WorkspaceViewModel(PrintClient api, LoginResult login, PrintDialogs dialogs, Action signedOut)
    {
        this.api = api;
        this.dialogs = dialogs;
        actor = login.User;
        IsStaff = login.User.Role is Roles.Admin or Roles.Manager;
        IsAdmin = login.User.Role == Roles.Admin;
        SessionCaption = $"{PrintText.Role(actor.Role)} · {actor.DisplayName}";
        selectedFilter = Filters[0];

        RefreshOrders = Run(_ => LoadOrders());
        RefreshCustomers = Run(_ => LoadCustomers(), () => IsStaff);
        RefreshStatistics = Run(async _ =>
        {
            Statistics = await api.Statistics();
            Changed(nameof(Statuses));
            Changed(nameof(AverageDays));
        });
        RefreshUsers = Run(_ => LoadUsers(), () => IsAdmin);
        NewUser = Run(async _ =>
        {
            if (dialogs.User() is not { } input)
                return;
            await api.SaveUser(input);
            await LoadUsers();
            Message = "Учётная запись создана.";
        }, () => IsAdmin);

        ToggleUserAccess = Run(async _ =>
        {
            var user = SelectedUser!.User;
            bool disable = !user.IsDisabled;
            if (!dialogs.ConfirmUserAccess(user.DisplayName, disable))
                return;
            await api.SetUserAccess(user.Id, disable);
            await LoadUsers(user.Id);
            Message = disable ? "Вход для сотрудника закрыт." : "Вход для сотрудника разрешён.";
        }, () => IsAdmin && SelectedUser is { } row && row.User.Id != actor.Id);

        NewOrder = Run(_ => SaveOrder(null), () => IsStaff);
        EditOrder = Run(_ => SaveOrder(SelectedOrder!.Order),
            () => IsStaff && SelectedOrder is { } row && !OrderRules.IsFinal(row.Order.Status));
        Details = Run(_ =>
        {
            dialogs.ShowOrder(SelectedOrder!.Order);
            return Task.CompletedTask;
        }, () => SelectedOrder is not null);
        Transition = Run(async _ =>
        {
            var order = SelectedOrder!.Order;
            if (dialogs.Transition(order, actor.Role) is not { } input)
                return;
            await api.Transition(order.Id, input);
            ClientLog.Info($"Заказ {order.Id}: переход в {input.Status}");
            await LoadOrders(order.Id);
            Message = $"Заказ №{order.Id} переведён в статус «{PrintText.Status(input.Status)}».";
        }, () => SelectedOrder is { } row && OrderRules.Available(row.Order.Status, actor.Role).Count != 0);

        NewCustomer = Run(_ => SaveCustomer(null), () => IsStaff);
        EditCustomer = Run(_ => SaveCustomer(SelectedCustomer), () => IsStaff && SelectedCustomer is not null);
        Import = Run(async _ =>
        {
            if (dialogs.OpenCsv() is not { } path)
                return;
            if (new FileInfo(path).Length > PrintFiles.MaxImportBytes)
                throw new RuleException("Размер CSV превышает 1 МБ.");
            try
            {
                var result = await api.Import(await File.ReadAllBytesAsync(path));
                await LoadCustomers();
                ClientLog.Info($"Импортировано заказчиков: {result.Imported}");
                Message = $"Импортировано заказчиков: {result.Imported}.";
            }
            catch (CsvImportException error)
            {
                ClientLog.Warning($"Отклонён CSV: ошибок {error.Errors.Count}");
                dialogs.ShowImportErrors(error.Errors);
                Message = $"Импорт отклонён. Ошибок: {error.Errors.Count}.";
            }
        }, () => IsStaff);
        Export = Run(async selectedFormat =>
        {
            var format = (string)selectedFormat!;
            if (dialogs.SaveExport(format) is not { } path)
                return;
            var timer = Stopwatch.StartNew();
            await File.WriteAllBytesAsync(path, await api.Export(format, SelectedFilter?.Status, Search));
            timer.Stop();
            Debug.WriteLine($"Экспорт {format}: {timer.ElapsedMilliseconds} мс");
            Message = $"Заказы выгружены в {Path.GetFileName(path)}.";
        }, () => IsStaff);
        Logout = Run(async _ =>
        {
            orderLoad?.Cancel();
            try
            {
                await api.Logout();
            }
            catch (ClientException error)
            {
                ClientLog.Write(error);
            }
            finally
            {
                signedOut();
            }
        });
    }

    private async void RefreshFilteredOrders(TimeSpan delay)
    {
        try
        {
            await LoadOrders(delay: delay);
        }
        catch (ClientException error)
        {
            Message = error.Message;
        }
        catch (Exception error)
        {
            ClientLog.Write(error);
            Message = "Не удалось обновить список заказов.";
        }
    }

    private async Task LoadOrders(int? id = null, TimeSpan delay = default)
    {
        orderLoad?.Cancel();
        using var request = new CancellationTokenSource();
        orderLoad = request;
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, request.Token);
            var requestedId = id ?? SelectedOrder?.Order.Id;
            var result = await api.Orders(SelectedFilter?.Status, Search, request.Token);
            request.Token.ThrowIfCancellationRequested();
            Orders = result.Select(order => new OrderRow(order)).ToArray();
            SelectedOrder = Orders.FirstOrDefault(row => row.Order.Id == requestedId);
            Changed(nameof(OrderCount));
            Changed(nameof(EmptyOrders));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // A new search, filter selection or logout replaced this request.
        }
        finally
        {
            if (ReferenceEquals(orderLoad, request))
                orderLoad = null;
        }
    }

    private async Task LoadUsers(int? id = null)
    {
        int? selectedId = id ?? SelectedUser?.User.Id;
        Users = (await api.Users(false)).Select(user => new UserRow(user)).ToArray();
        SelectedUser = Users.FirstOrDefault(row => row.User.Id == selectedId);
    }

    private async Task LoadCustomers() => Customers = await api.Customers();

    private async Task SaveCustomer(CustomerInfo? customer)
    {
        if (dialogs.Customer(customer) is not { } input)
            return;
        await api.SaveCustomer(customer?.Id, input);
        await LoadCustomers();
        Message = "Данные заказчика сохранены.";
    }

    private async Task SaveOrder(OrderInfo? order)
    {
        var availableCustomers = await api.Customers();
        if (availableCustomers.Length == 0)
        {
            Tab = 1;
            await LoadCustomers();
            throw new RuleException("Сначала добавьте заказчика в справочник.");
        }
        var printers = await api.Users(true);
        if (dialogs.Order(order, availableCustomers, printers) is not { } input)
            return;
        await api.SaveOrder(order?.Id, input);
        await LoadOrders(order?.Id);
        Message = "Заказ сохранён.";
    }
}
