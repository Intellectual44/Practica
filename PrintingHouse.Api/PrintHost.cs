using System.Security.Cryptography;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using PrintingHouse.Core;
using PrintingHouse.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
#if NET8_0
using Microsoft.OpenApi.Models;
#else
using Microsoft.OpenApi;
#endif
using Serilog;

namespace PrintingHouse.Api;

public static class PrintHost
{
    public static WebApplication Build(string[] args, X509Certificate2? desktopCertificate = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(PrintHost).Assembly.GetName().Name
        });

        if (desktopCertificate is not null)
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(desktopCertificate)));
        else
        {
            string urls = builder.Configuration["urls"] ?? "https://localhost:7142";
            if (urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(url => !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Для API указывайте только HTTPS-адреса.");
            builder.WebHost.UseUrls(urls);
        }

        int loginLimit = builder.Configuration.GetValue("Security:LoginAttemptsPerMinute", 5);
        if (loginLimit is < 1 or > 1000)
            throw new InvalidOperationException("Security:LoginAttemptsPerMinute должен быть от 1 до 1000.");
        string? configuredKey = builder.Configuration["Jwt:Key"];
        if (configuredKey is not null && Encoding.UTF8.GetByteCount(configuredKey) < 32)
            throw new InvalidOperationException("Jwt:Key должен содержать не менее 32 байтов.");

        var path = DatabasePath(builder.Configuration);
        builder.Services.AddSingleton(new PrintStartupOptions(
            builder.Configuration.GetValue<bool?>("Print:SeedDemo") ??
            (desktopCertificate is not null && builder.Configuration["Print:Database"] is null)));
        builder.Services.AddDbContext<PrintDb>(options => PrintDatabase.Configure(options, path));
        builder.Services.AddScoped<PrintStore>();
        builder.Services.AddScoped<IOrderRepository>(services => services.GetRequiredService<PrintStore>());
        builder.Services.AddScoped<ICustomerRepository>(services => services.GetRequiredService<PrintStore>());
        builder.Services.AddScoped<IUserRepository>(services => services.GetRequiredService<PrintStore>());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddMemoryCache();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddProblemDetails();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            const string bearer = "Bearer";
            options.AddSecurityDefinition(bearer, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT"
            });
#if NET8_0
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = bearer }
                }] = Array.Empty<string>()
            });
#else
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(bearer, document)] = []
            });
#endif
        });
        builder.Services.AddControllers().AddJsonOptions(options =>
        {
            foreach (var converter in PrintFiles.JsonOptions.Converters)
                options.JsonSerializerOptions.Converters.Add(converter);
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            foreach (var converter in PrintFiles.JsonOptions.Converters)
                options.SerializerOptions.Converters.Add(converter);
        });

        var key = new SymmetricSecurityKey(configuredKey is null
            ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(configuredKey));
        builder.Services.AddSingleton(new SessionTokens(key));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = SessionTokens.Issuer,
                ValidAudience = SessionTokens.Audience,
                IssuerSigningKey = key,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.Zero,
                NameClaimType = "login",
                RoleClaimType = "role"
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var id = context.Principal?.FindFirst("jti")?.Value;
                    var revoked = context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
                    if (id is null || revoked.TryGetValue(id, out _))
                        context.Fail("Сеанс завершён.");
                    else
                    {
                        var actor = SessionTokens.User(context.Principal!);
                        var db = context.HttpContext.RequestServices.GetRequiredService<PrintDb>();
                        if (!await db.Users.AnyAsync(user => user.Id == actor.Id && !user.IsDisabled,
                            context.HttpContext.RequestAborted))
                            context.Fail("Учётная запись отключена.");
                    }
                }
            };
        });
        builder.Services.AddAuthorizationBuilder().AddPolicy("Staff", policy =>
            policy.RequireRole(Roles.Admin, Roles.Manager));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("Login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = loginLimit,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true
                }));
        });

        builder.Services.AddSerilog((_, logger) => logger.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(Path.GetDirectoryName(path)!, "logs", "api-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, shared: true));

        var app = builder.Build();
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler(handler => handler.Run(HandleError));
        app.UseStatusCodePages();
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapPrintEndpoints();
        if (app.Environment.IsDevelopment())
            app.MapGet("/api/debug/requests", (ClaimsPrincipal user, IOrderRepository orders, CancellationToken ct) =>
                orders.FindOrders(SessionTokens.User(user), null, null, ct))
                .RequireAuthorization(policy => policy.RequireRole(Roles.Admin));
        return app;
    }

    public static async Task Initialize(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await PrintDatabase.Initialize(scope.ServiceProvider.GetRequiredService<PrintDb>(),
            DatabasePath(app.Configuration), app.Services.GetRequiredService<PrintStartupOptions>().SeedDemo);
    }

    private sealed record PrintStartupOptions(bool SeedDemo);

    private static string DatabasePath(IConfiguration config) =>
        Path.GetFullPath(config["Print:Database"] ?? PrintDatabase.DefaultPath);

    private static Task HandleError(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        return (error switch
        {
            CsvImportException csv => Results.Problem(statusCode: 400, title: "Ошибка импорта CSV",
                detail: csv.Message, extensions: new Dictionary<string, object?> { ["errors"] = csv.Errors }),
            OrderAccessException => Results.Problem(statusCode: 403, title: "Действие запрещено", detail: error.Message),
            UserConflictException => Results.Problem(statusCode: 409, title: "Конфликт учётной записи", detail: error.Message),
            RuleException => Results.Problem(statusCode: 400, title: "Проверьте данные", detail: error.Message),
            BadHttpRequestException bad => Results.Problem(statusCode: bad.StatusCode, title: "Некорректный запрос"),
            OrderConflictException or DbUpdateConcurrencyException => Results.Problem(statusCode: 409, title: "Заказ изменился",
                detail: "Обновите список, откройте заказ и повторите действие."),
            DbUpdateException => Results.Problem(statusCode: 500, title: "Ошибка сохранения данных"),
            _ => Results.Problem(statusCode: 500, title: "Внутренняя ошибка типографии")
        }).ExecuteAsync(context);
    }
}
