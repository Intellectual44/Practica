using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using PrintingHouse.Api;
using Microsoft.AspNetCore.Builder;

namespace PrintingHouse.Desktop;

public partial class App : Application
{
    private WebApplication? host;
    private HttpClient? http;
    private Task<PrintClient>? client;
    private X509Certificate2? certificate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ClientLog.Start();
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("ru-RU")));
        client = Task.Run(StartClient);
        ShowLogin();
    }

    private async Task<PrintClient> StartClient()
    {
        try
        {
            var preparedClient = await StartLocalApi();
            ClientLog.Info("Локальный API запущен, база готова.");
            return preparedClient;
        }
        catch (Exception error)
        {
            ClientLog.Warning($"Локальный API не запустился: {error.Message}");
            ClientLog.Write(error);
            throw;
        }
    }

    private async Task<PrintClient> StartLocalApi()
    {
        certificate = LoopbackCertificate.Create();
        var expected = certificate.GetCertHash(HashAlgorithmName.SHA256);
        host = PrintHost.Build([], certificate);
        await PrintHost.Initialize(host);
        await host.StartAsync();
        var transport = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (request, presented, _, _) =>
                request.RequestUri is { Scheme: "https", Host: "127.0.0.1" } &&
                presented is not null &&
                CryptographicOperations.FixedTimeEquals(expected, presented.GetCertHash(HashAlgorithmName.SHA256))
        };
        http = new HttpClient(transport)
        {
            BaseAddress = new Uri(host.Urls.Single()),
            Timeout = TimeSpan.FromSeconds(12)
        };
        return new PrintClient(http);
    }

    private void ShowLogin()
    {
        var model = new LoginViewModel(client!, ShowWorkspace);
        SwitchTo(new LoginWindow { DataContext = model });
    }

    private void ShowWorkspace(PrintClient api, Core.LoginResult login)
    {
        var shell = new WorkspaceWindow();
        var model = new WorkspaceViewModel(api, login, new PrintDialogs(shell), ShowLogin);
        shell.DataContext = model;
        SwitchTo(shell);
        model.RefreshOrders.Execute(null);
    }

    private void SwitchTo(Window next)
    {
        var previous = MainWindow;
        MainWindow = next;
        next.Show();
        if (previous is not null && previous != next)
            previous.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (client is not null)
                Task.WhenAny(client, Task.Delay(1500)).GetAwaiter().GetResult();
            if (client?.IsCompleted == true && host is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                host.StopAsync(timeout.Token).GetAwaiter().GetResult();
                host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception error)
        {
            ClientLog.Write(error);
        }
        finally
        {
            http?.Dispose();
            if (client?.IsCompleted == true)
                certificate?.Dispose();
            ClientLog.Info("Завершение работы WPF-клиента");
            base.OnExit(e);
        }
    }
}
