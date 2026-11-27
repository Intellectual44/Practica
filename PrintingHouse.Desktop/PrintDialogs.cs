using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using PrintingHouse.Core;
using Microsoft.Win32;

namespace PrintingHouse.Desktop;

internal sealed class PrintDialogs(Window owner)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public OrderInput? Order(OrderInfo? order, CustomerInfo[] customers, UserInfo[] printers)
    {
        var form = new Form(owner, order is null ? "Новый заказ" : $"Заказ №{order.Id}");
        var customer = new ComboBox { ItemsSource = customers, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
        customer.SelectedValue = order?.CustomerId ?? customers[0].Id;
        form.Add("CustomerSelect", "_Заказчик", customer);

        var title = form.Text("TitleInput", "_Продукция", order?.Title, 180);
        var specification = form.Text("AddressInput", "_Параметры печати", order?.Specification, 220);
        var quantity = form.Text("QuantityInput", "_Тираж, экз.", order?.Quantity.ToString(CultureInfo.InvariantCulture), 7);
        var layout = form.Text("LayoutReferenceInput", "_Ссылка или имя файла макета", order?.LayoutReference, 300);
        var notes = form.Text("NotesInput", "_Примечания", order?.Notes, 2000, multiline: true);

        var assignees = new[] { new UserInfo(0, "", "Без назначения", "") }.Concat(printers).ToArray();
        var printer = new ComboBox { ItemsSource = assignees, DisplayMemberPath = "DisplayName", SelectedValuePath = "Id" };
        printer.SelectedValue = order?.PrinterId ?? 0;
        form.Add("PrinterSelect", "_Печатник", printer);

        var amount = form.Text("AmountInput", "С_умма, ₽", order?.Amount?.ToString("0.##", Russian), 24);
        var delivery = form.Text("DeliveryDateInput", "_Срок выдачи (ДД.ММ.ГГГГ)",
            order?.DueDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), 10);

        return form.Save("Сохранить", () =>
        {
            if (customer.SelectedValue is not int customerId)
                throw new RuleException("Выберите заказчика.");

            int? printerId = printer.SelectedValue is int id && id > 0 ? id : null;
            return new OrderInput(customerId, InputRules.Required(title.Text, "Продукция", 180),
                InputRules.Required(specification.Text, "Параметры печати", 220), notes.Text, printerId,
                InputRules.ParseAmount(amount.Text), InputRules.ParseDueDate(delivery.Text), order?.Version,
                InputRules.ParseQuantity(quantity.Text), layout.Text);
        });
    }

    public CustomerInput? Customer(CustomerInfo? customer)
    {
        var form = new Form(owner, customer is null ? "Новый заказчик" : "Редактирование заказчика");
        var name = form.Text("CustomerNameInput", "_Имя", customer?.Name, 150);
        var phone = form.Text("CustomerPhoneInput", "_Телефон", customer?.Phone, 40);
        var email = form.Text("CustomerEmailInput", "_Эл. почта", customer?.Email, 150);
        return form.Save("Сохранить", () => InputRules.Customer(new CustomerInput(name.Text, phone.Text, email.Text)));
    }

    public TransitionInput? Transition(OrderInfo order, string role)
    {
        var form = new Form(owner, $"Смена статуса · заказ №{order.Id}");
        form.Summary($"Сейчас: {PrintText.Status(order.Status)}");
        var options = OrderRules.Available(order.Status, role).Select(status => new StatusOption(status, PrintText.Status(status))).ToArray();
        var status = new ComboBox { ItemsSource = options, DisplayMemberPath = "Name", SelectedIndex = 0 };
        form.Add("NextStatusSelect", "Новый _статус", status);
        var reason = form.Text("CancellationReasonInput", "_Причина отмены", null, 500, multiline: true);
        reason.IsEnabled = ((StatusOption?)status.SelectedItem)?.Status == OrderStatus.Cancelled;
        status.SelectionChanged += (_, _) => reason.IsEnabled =
            ((StatusOption?)status.SelectedItem)?.Status == OrderStatus.Cancelled;
        return form.Save("Применить", () =>
        {
            var next = ((StatusOption?)status.SelectedItem)?.Status ?? throw new RuleException("Выберите статус.");
            return new TransitionInput(next, next == OrderStatus.Cancelled
                ? InputRules.Required(reason.Text, "Причина отмены", 500) : null, order.Version);
        });
    }

    public void ShowOrder(OrderInfo order)
    {
        var form = new Form(owner, $"Заказ №{order.Id} · {order.Title}");
        form.Summary($"Статус: {PrintText.Status(order.Status)}");
        form.Summary($"Заказчик: {order.Customer}  ·  {order.Phone}");
        form.Summary($"Параметры печати: {order.Specification}");
        form.Summary($"Тираж: {order.Quantity:N0} экз.");
        form.Summary($"Макет: {order.LayoutReference ?? "—"}");
        form.Summary($"Печатник: {order.Printer ?? "—"}");
        form.Summary($"Сумма: {(order.Amount is { } amount ? amount.ToString("N2", Russian) + " ₽" : "—")}");
        form.Summary($"Срок выдачи: {order.DueDate?.ToString("dd.MM.yyyy") ?? "—"}");
        form.Summary($"Создан: {order.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}");
        if (order.CompletedAt is { } completed)
            form.Summary($"Выдан: {completed.ToLocalTime():dd.MM.yyyy HH:mm}");
        if (!string.IsNullOrEmpty(order.CancellationReason))
            form.Summary($"Причина отмены: {order.CancellationReason}");
        if (!string.IsNullOrEmpty(order.Notes))
            form.Summary($"Примечания: {order.Notes}");
        form.Show();
    }

    public UserInput? User()
    {
        var form = new Form(owner, "Новый сотрудник");
        var name = form.Text("StaffNameInput", "_Имя сотрудника", null, 120);
        var login = form.Text("StaffLoginInput", "_Логин", null, 80);
        var roles = new[] { Roles.Manager, Roles.Printer, Roles.Admin };
        var role = new ComboBox
        {
            ItemsSource = roles.Select(value => new { Value = value, Name = PrintText.Role(value) }),
            DisplayMemberPath = "Name", SelectedValuePath = "Value", SelectedIndex = 0
        };
        form.Add("StaffRoleSelect", "_Роль", role);
        var password = new PasswordBox { MaxLength = 128 };
        form.Add("StaffPasswordInput", "_Пароль", password);
        form.Summary("От 10 символов, с буквой и цифрой, без пробелов.");
        return form.Save("Создать", () => InputRules.User(
            new UserInput(login.Text, name.Text, (string)role.SelectedValue, password.Password)));
    }

    public bool ConfirmUserAccess(string name, bool disable)
    {
        var form = new Form(owner, disable ? "Закрыть вход" : "Разрешить вход");
        form.Summary(disable ? $"Закрыть вход для сотрудника «{name}»?" : $"Разрешить вход для сотрудника «{name}»?");
        return form.Save(disable ? "Закрыть вход" : "Разрешить вход", () => name) is not null;
    }

    public string? OpenCsv()
    {
        var dialog = new OpenFileDialog { Filter = "Файлы CSV (*.csv)|*.csv", CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public void ShowImportErrors(IReadOnlyList<CsvRowError> errors)
    {
        var form = new Form(owner, "Ошибки импорта CSV");
        form.Summary($"Записи не добавлены. Исправьте ошибки в файле ({errors.Count}).");
        form.Add("ImportErrorReport", "Строки с ошибками", new TextBox
        {
            IsReadOnly = true,
            MaxHeight = 360,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join(Environment.NewLine, errors.Select(row => $"{row.Line}: {row.Message}"))
        });
        form.Show();
    }

    public string? SaveExport(string format)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"orders-{DateTime.Today:yyyy-MM-dd}.{format}",
            Filter = $"{format.ToUpperInvariant()} (*.{format})|*.{format}",
            DefaultExt = format,
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private sealed class Form
    {
        private readonly Window window;
        private readonly StackPanel fields = new();
        private readonly StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly TextBlock message = new()
        {
            Foreground = new SolidColorBrush(Color.FromRgb(157, 44, 39)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 9)
        };

        public Form(Window owner, string title)
        {
            window = new Window
            {
                Owner = owner, Title = title, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 540, SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height - 40,
                ResizeMode = ResizeMode.NoResize, Style = (Style)Application.Current.FindResource(typeof(Window))
            };
            var layout = new DockPanel { Margin = new Thickness(22) };
            var buttons = new StackPanel();
            buttons.Children.Add(message);
            buttons.Children.Add(footer);
            DockPanel.SetDock(buttons, Dock.Bottom);
            layout.Children.Add(buttons);
            layout.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            window.Content = layout;
        }

        public void Add(string id, string caption, Control control)
        {
            fields.Children.Add(new Label { Content = caption, Target = control, Padding = new Thickness(0, 7, 0, 4) });
            AutomationProperties.SetAutomationId(control, id);
            fields.Children.Add(control);
        }

        public TextBox Text(string id, string caption, string? value, int max, bool multiline = false)
        {
            var control = new TextBox
            {
                Text = value ?? "", MaxLength = max, AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap
            };
            if (multiline)
            {
                control.MinHeight = 55;
                control.MaxHeight = 90;
                control.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            Add(id, caption, control);
            return control;
        }

        public void Summary(string value) =>
            fields.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 8) });

        public T? Save<T>(string label, Func<T> value) where T : class
        {
            T? result = null;
            var cancel = new Button { Content = "Закрыть", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            var save = new Button { Content = label, IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
            save.Click += (_, _) =>
            {
                try
                {
                    result = value();
                    window.DialogResult = true;
                }
                catch (RuleException error)
                {
                    message.Text = error.Message;
                }
            };
            AutomationProperties.SetAutomationId(cancel, "FormCloseButton");
            AutomationProperties.SetAutomationId(save, "FormSaveButton");
            footer.Children.Add(cancel);
            footer.Children.Add(save);
            return window.ShowDialog() == true ? result : null;
        }

        public void Show()
        {
            var close = new Button { Content = "Закрыть", IsCancel = true, IsDefault = true };
            AutomationProperties.SetAutomationId(close, "FormCloseButton");
            footer.Children.Add(close);
            window.ShowDialog();
        }
    }
}
