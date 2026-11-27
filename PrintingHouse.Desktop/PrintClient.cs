using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PrintingHouse.Core;

namespace PrintingHouse.Desktop;

internal sealed class PrintClient(HttpClient http)
{
    private string? token;

    public async Task<LoginResult> Login(string login, string password)
    {
        var result = await Json<LoginResult>(HttpMethod.Post, "api/auth/login", new LoginInput(login, password));
        token = result.Token;
        return result;
    }

    public async Task Logout()
    {
        try
        {
            await Send(HttpMethod.Post, "api/auth/logout");
        }
        finally
        {
            token = null;
        }
    }

    public Task<OrderInfo[]> Orders(OrderStatus? status, string? search, CancellationToken ct) =>
        Json<OrderInfo[]>(HttpMethod.Get, Filter("api/orders", status, search), ct: ct);

    public Task<CustomerInfo[]> Customers() => Json<CustomerInfo[]>(HttpMethod.Get, "api/customers");

    public Task<UserInfo[]> Users(bool printersOnly) =>
        Json<UserInfo[]>(HttpMethod.Get, printersOnly ? "api/users/printers" : "api/users");

    public Task SaveUser(UserInput input) => Send(HttpMethod.Post, "api/users", input);

    public Task SetUserAccess(int id, bool isDisabled) =>
        Send(HttpMethod.Put, $"api/users/{id}/access", new UserAccessInput(isDisabled));

    public Task<PrintStatistics> Statistics() =>
        Json<PrintStatistics>(HttpMethod.Get, "api/orders/statistics");

    public Task SaveOrder(int? id, OrderInput order) =>
        id.HasValue ? Send(HttpMethod.Put, $"api/orders/{id}", order) : Send(HttpMethod.Post, "api/orders", order);

    public Task SaveCustomer(int? id, CustomerInput customer) =>
        id.HasValue ? Send(HttpMethod.Put, $"api/customers/{id}", customer) : Send(HttpMethod.Post, "api/customers", customer);

    public Task Transition(int id, TransitionInput input) =>
        Send(HttpMethod.Post, $"api/orders/{id}/transition", input);

    public async Task<ImportResult> Import(byte[] csv)
    {
        using var message = new ByteArrayContent(csv);
        message.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
        using var response = await Request(HttpMethod.Post, "api/customers/import", message);
        return (await response.Content.ReadFromJsonAsync<ImportResult>(PrintFiles.JsonOptions))!;
    }

    public async Task<byte[]> Export(string format, OrderStatus? status, string? search)
    {
        using var response = await Request(HttpMethod.Get, Filter($"api/orders/export/{format}", status, search));
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task Send(HttpMethod method, string path, object? input = null)
    {
        using var response = await Request(method, path, Content(input));
    }

    private async Task<T> Json<T>(HttpMethod method, string path, object? input = null, CancellationToken ct = default)
    {
        using var response = await Request(method, path, Content(input), ct);
        return await response.Content.ReadFromJsonAsync<T>(PrintFiles.JsonOptions, ct) ??
            throw new ClientException("Сервер вернул пустой ответ.");
    }

    private async Task<HttpResponseMessage> Request(HttpMethod method, string path, HttpContent? content = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException error) when (!ct.IsCancellationRequested)
        {
            ClientLog.Write(error);
            throw new ClientException("Истекло время ожидания локального сервера. Повторите действие.");
        }
        catch (HttpRequestException error)
        {
            ClientLog.Write(error);
            throw new ClientException("Локальный сервер не ответил. Повторите действие.");
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            if (path == "api/customers/import" && response.StatusCode == HttpStatusCode.BadRequest)
            {
                var report = await response.Content.ReadFromJsonAsync<CsvProblem>(PrintFiles.JsonOptions, ct);
                if (report?.Errors is { Length: > 0 } errors)
                    throw new CsvImportException(errors);
                throw new ClientException(report?.Detail ?? report?.Title ?? "Не удалось прочитать CSV.");
            }
            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized when path == "api/auth/login" => "Неверный логин или пароль.",
                HttpStatusCode.Unauthorized => "Сеанс завершён. Выйдите и войдите заново.",
                HttpStatusCode.Forbidden => "У вашей роли нет доступа к этому действию.",
                HttpStatusCode.NotFound => "Запись недоступна. Обновите список.",
                HttpStatusCode.TooManyRequests => "Слишком много попыток. Повторите вход через минуту.",
                _ => await ReadProblem(response, ct)
            };
            throw new ClientException(message);
        }
    }

    private static async Task<string> ReadProblem(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { } message)
                return message;
            if (json.RootElement.TryGetProperty("title", out var title) && title.GetString() is { } summary)
                return summary;
        }
        catch (JsonException)
        {
            // Some framework responses have an empty body.
        }
        return $"Не удалось выполнить действие. Код ответа {(int)response.StatusCode}.";
    }

    private static HttpContent? Content(object? input) =>
        input is null ? null : JsonContent.Create(input, options: PrintFiles.JsonOptions);

    private static string Filter(string path, OrderStatus? status, string? search)
    {
        return $"{path}?q={Uri.EscapeDataString(search?.Trim() ?? "")}" +
            (status is null ? "" : $"&status={status}");
    }

    private sealed record CsvProblem(string? Title, string? Detail, CsvRowError[]? Errors);
}

internal sealed class ClientException(string message) : Exception(message);
