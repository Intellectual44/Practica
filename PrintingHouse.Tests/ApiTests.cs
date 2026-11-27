using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using PrintingHouse.Core;
using PrintingHouse.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PrintingHouse.Tests;

public sealed partial class ApiTests
{
    private static readonly string TestPassword = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task UnreadableStoredHashRejectsLoginWithoutServerError()
    {
        using var server = new DatabaseFactory();
        using var client = server.CreateClient();
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintDb>();
        var user = await db.Users.FindAsync(2);
        user!.PasswordHash = "invalid-hash";
        await db.SaveChangesAsync();

        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginInput("press.manager", TestPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("invalid-hash", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AccessIsEnforcedForAllRolesAndLogoutRevokesToken()
    {
        using var server = new DatabaseFactory();
        using var anonymous = server.CreateClient();
        using var admin = await SignIn(server, "press.admin", TestPassword, Roles.Admin);
        using var manager = await SignIn(server, "press.manager", TestPassword, Roles.Manager);
        using var first = await SignIn(server, "press.operator", TestPassword, Roles.Printer);
        using var second = await SignIn(server, "press.operator2", TestPassword, Roles.Printer);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(4, (await Get<UserInfo[]>(admin, "/api/users")).Length);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(2, (await Get<UserInfo[]>(manager, "/api/users/printers")).Length);

        int customer = await CreateCustomer(manager);
        int order1 = await CreateOrder(manager, customer, 3, "Листовка");
        int order2 = await CreateOrder(admin, customer, 4, "Буклет");

        Assert.Equal(2, (await Get<OrderInfo[]>(admin, "/api/orders")).Length);
        Assert.Equal(order1, Assert.Single(await Get<OrderInfo[]>(first, "/api/orders")).Id);
        Assert.Equal(order2, Assert.Single(await Get<OrderInfo[]>(second, "/api/orders")).Id);
        Assert.Equal(1, (await Get<PrintStatistics>(first, "/api/orders/statistics")).Total);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync($"/api/orders/{order2}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await Post(first, $"/api/orders/{order2}/transition",
            new TransitionInput(OrderStatus.Layout, null, 1)));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(first, "/api/orders", new { }));
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync("/api/orders/export/json")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await Post(first, $"/api/orders/{order1}/transition",
            new TransitionInput(OrderStatus.Layout, null, 1)));

        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task OrderAdvancesWithValidationAndRejectsStaleEdits()
    {
        using var server = new DatabaseFactory();
        using var manager = await SignIn(server, "press.manager", TestPassword, Roles.Manager);
        int customer = await CreateCustomer(manager);
        int order = await CreateOrder(manager, customer, null, "Буклет");
        var path = $"/api/orders/{order}";
        var state = await Get<OrderInfo>(manager, path);
        Assert.Equal(OrderStatus.Accepted, state.Status);
        Assert.Equal(HttpStatusCode.BadRequest, await Step(OrderStatus.Ready, 1));
        Assert.Equal(HttpStatusCode.NoContent, await Step(OrderStatus.Layout, 1));
        Assert.Equal(HttpStatusCode.Conflict, await Step(OrderStatus.Approved, 1));
        Assert.Equal(HttpStatusCode.BadRequest, await Step(OrderStatus.Approved, 2));
        Assert.Equal(HttpStatusCode.NoContent, await Edit(2, null, null, null));
        Assert.Equal(HttpStatusCode.NoContent, await Step(OrderStatus.Approved, 3));
        Assert.Equal(HttpStatusCode.BadRequest, await Step(OrderStatus.Printing, 4));
        Assert.Equal(HttpStatusCode.NoContent, await Edit(4, 3, null, null));
        Assert.Equal(HttpStatusCode.NoContent, await Step(OrderStatus.Printing, 5));
        Assert.Equal(HttpStatusCode.BadRequest, await Edit(6, null, null, null));
        using var printer = await SignIn(server, "press.operator", TestPassword, Roles.Printer);
        Assert.Equal(HttpStatusCode.NoContent, await Post(printer, path + "/transition",
            new TransitionInput(OrderStatus.Ready, null, 6)));
        Assert.Equal(HttpStatusCode.BadRequest, await Step(OrderStatus.Issued, 7));
        Assert.Equal(HttpStatusCode.NoContent, await Edit(7, 3, 36000, new DateOnly(2026, 11, 21)));
        Assert.Equal(HttpStatusCode.NoContent, await Step(OrderStatus.Issued, 8));
        Assert.Equal(HttpStatusCode.BadRequest, await Step(OrderStatus.Cancelled, 9, "Учебная отмена"));
        var statistics = await Get<PrintStatistics>(manager, "/api/orders/statistics");
        Assert.Equal(36000, statistics.IssuedAmount);
        Assert.NotNull(statistics.AverageDays);

        int cancelled = await CreateOrder(manager, customer, 4, "Обложка");
        Assert.Equal(HttpStatusCode.BadRequest, await Post(manager, $"/api/orders/{cancelled}/transition",
            new TransitionInput(OrderStatus.Cancelled, " ", 1)));
        Assert.Equal(HttpStatusCode.NoContent, await Post(manager, $"/api/orders/{cancelled}/transition",
            new TransitionInput(OrderStatus.Cancelled, "Отказ", 1)));
        Assert.Equal("Отказ", (await Get<OrderInfo>(manager, $"/api/orders/{cancelled}")).CancellationReason);

        Task<HttpStatusCode> Step(OrderStatus target, int version, string? reason = null) =>
            Post(manager, path + "/transition", new TransitionInput(target, reason, version));

        async Task<HttpStatusCode> Edit(int version, int? printer, decimal? amount, DateOnly? date)
        {
            using var response = await manager.PutAsJsonAsync(path,
                new OrderInput(customer, "Буклет", "А5, 4+4", null, printer, amount, date, version, 300, "booklet.pdf"),
                PrintFiles.JsonOptions);
            return response.StatusCode;
        }
    }

    [Fact]
    public async Task ImportsAtomicallyAndExportsFilteredOrders()
    {
        using var server = new DatabaseFactory();
        using var manager = await SignIn(server, "press.manager", TestPassword, Roles.Manager);
        using var wrong = new StringContent("Name;Phone;Email\nЗаказчик;123;\nОшибка;;", Encoding.UTF8, "text/csv");
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync("/api/customers/import", wrong)).StatusCode);
        Assert.Empty(await Get<CustomerInfo[]>(manager, "/api/customers"));

        using var csv = new StringContent("Name;Phone;Email\n\"Орлова, Анна\";123;anna@example.com\nПетров;456;",
            Encoding.UTF8, "text/csv");
        using var imported = await manager.PostAsync("/api/customers/import", csv);
        Assert.Equal(2, (await imported.Content.ReadFromJsonAsync<ImportResult>())!.Imported);

        var customers = await Get<CustomerInfo[]>(manager, "/api/customers");
        int exportId = await CreateOrder(manager, customers[0].Id, 3, "Каталог");
        await CreateOrder(manager, customers[1].Id, 4, "Листовка");
        var query = "?status=Accepted&q=" + Uri.EscapeDataString("катал");
        Assert.Equal(exportId, Assert.Single(await Get<OrderInfo[]>(manager, "/api/orders" + query)).Id);

        foreach (string format in new[] { "csv", "json", "xml" })
        {
            using var response = await manager.GetAsync($"/api/orders/export/{format}{query}");
            response.EnsureSuccessStatusCode();
            Assert.Equal($"orders.{format}", response.Content.Headers.ContentDisposition!.FileName);
            var document = await response.Content.ReadAsStringAsync();
            Assert.Contains("Каталог", format == "json"
                ? Assert.Single(System.Text.Json.JsonSerializer.Deserialize<OrderInfo[]>(document, PrintFiles.JsonOptions)!).Title
                : document);
            Assert.DoesNotContain("Листовка", document);
        }
    }

    [Fact]
    public async Task AdministratorCanCreateAndDisableStaff()
    {
        using var server = new DatabaseFactory();
        using var admin = await SignIn(server, "press.admin", TestPassword, Roles.Admin);
        using var manager = await SignIn(server, "press.manager", TestPassword, Roles.Manager);
        var input = new UserInput("new.printer", "Учебный печатник", Roles.Printer, TestPassword + "A1");
        Assert.Equal(HttpStatusCode.Forbidden, await Post(manager, "/api/users", input));

        using var created = await admin.PostAsJsonAsync("/api/users", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        int id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, await Post(admin, "/api/users", input));

        using var printer = await SignIn(server, input.Login, input.Password, Roles.Printer);
        using var updated = await admin.PutAsJsonAsync($"/api/users/{id}/access", new UserAccessInput(true));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var current = await printer.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);
    }

    private static async Task<HttpClient> SignIn(DatabaseFactory server, string login, string password, string role)
    {
        var http = server.CreateClient();
        using var response = await http.PostAsJsonAsync("/api/auth/login", new LoginInput(login, password));
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<LoginResult>())!;
        Assert.Equal(role, session.User.Role);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return http;
    }

    private static async Task<int> CreateCustomer(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/customers", new CustomerInput("Заказчик", "123", null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private static async Task<int> CreateOrder(HttpClient client, int customer, int? printer, string title)
    {
        using var response = await client.PostAsJsonAsync("/api/orders",
            new OrderInput(customer, title, "А5, 4+4", null, printer, null, null, Quantity: 300));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private static async Task<T> Get<T>(HttpClient http, string url) =>
        (await http.GetFromJsonAsync<T>(url, PrintFiles.JsonOptions))!;

    private static async Task<HttpStatusCode> Post(HttpClient http, string url, object value)
    {
        using var result = await http.PostAsJsonAsync(url, value, PrintFiles.JsonOptions);
        return result.StatusCode;
    }

    private sealed record Created(int Id);

    private sealed class DatabaseFactory : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "PrintingHouse.Tests", Guid.NewGuid().ToString("N"));

        protected override IHost CreateHost(IHostBuilder builder)
        {
            const string setting = "Print__Database";
            string? previous = Environment.GetEnvironmentVariable(setting);
            try
            {
                Environment.SetEnvironmentVariable(setting, Path.Combine(directory, "integration.db"));
                var host = base.CreateHost(builder);
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PrintDb>();
                var salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                var hash = salt + ":" + PrintDatabase.Hash(TestPassword, salt);
                foreach (var user in db.Users)
                    user.PasswordHash = hash;
                db.SaveChanges();
                return host;
            }
            finally
            {
                Environment.SetEnvironmentVariable(setting, previous);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(directory))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
