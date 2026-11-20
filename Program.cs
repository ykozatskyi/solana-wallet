using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SolanaWalletService.Configuration;
using SolanaWalletService.Models;
using SolanaWalletService.Payouts;
using SolanaWalletService.Persistence;
using SolanaWalletService.Services;
using SolanaWalletService.Solana;
using SolanaWalletService.Telegram;
using Telegram.Bot;

namespace SolanaWalletService;

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--self-check"))
        {
            Services.SolanaWalletService.SelfCheck();
            BalanceWatcher.SelfCheck();
            PayoutVerifier.SelfCheck();
            Console.WriteLine("Self-check ok.");
            return;
        }

        EnvFileLoader.Load();

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://127.0.0.1:5101");
        builder.Configuration["urls"] = "http://127.0.0.1:5101";
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
        });

        var appOptions = AppOptions.Create(builder.Configuration);
        Services.SolanaWalletService.SelfCheck();
        BalanceWatcher.SelfCheck();
        PayoutVerifier.SelfCheck();
        builder.Services.AddSingleton(appOptions);
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Logging.AddFilter("System.Net.Http.HttpClient.telegram", LogLevel.None);
        builder.Logging.AddFilter("System.Net.Http.HttpClient.ITelegramBotClient", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Npgsql", LogLevel.Warning);

        builder.Services.AddHttpClient(SolanaRpcClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SolanaWalletService/1.0");
        });
        builder.Services.AddSingleton<SolanaRpcClient>();
        builder.Services.AddSingleton<Services.SolanaWalletService>();
        builder.Services.AddSingleton<IWalletSigner>(serviceProvider => SolanaWalletSigner.Create(
            serviceProvider.GetRequiredService<AppOptions>(),
            serviceProvider.GetRequiredService<ILogger<SolanaWalletSigner>>()));
        builder.Services.AddSingleton(serviceProvider =>
        {
            var gate = new WalletSigningGate();
            gate.ApplyLocal(serviceProvider.GetRequiredService<IWalletSigner>().Identity);
            return gate;
        });
        builder.Services.AddDbContextFactory<WalletDbContext>(options =>
            options.UseNpgsql(appOptions.DatabaseConnectionString));
        builder.Services.AddSingleton<DepositStore>();
        builder.Services.AddSingleton(serviceProvider => new PayoutStore(
            serviceProvider.GetRequiredService<IDbContextFactory<WalletDbContext>>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<PayoutService>();
        builder.Services.AddSingleton<PayoutProcessor>();
        builder.Services.AddHostedService<PayoutWorker>();

        if (appOptions.TelegramBotToken is not null)
        {
            builder.Services.AddHttpClient("telegram")
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(40))
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            builder.Services.AddSingleton<ITelegramBotClient>(serviceProvider =>
            {
                var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("telegram");
                var options = serviceProvider.GetRequiredService<AppOptions>();
                return new TelegramBotClient(options.TelegramBotToken!, httpClient);
            });
            builder.Services.AddSingleton<IPayoutNotifier, TelegramPayoutNotifier>();
            builder.Services.AddSingleton<TelegramUpdateHandler>();
            builder.Services.AddHostedService<TelegramBotWorker>();
        }
        else
        {
            builder.Services.AddSingleton<IPayoutNotifier, LogPayoutNotifier>();
        }

        builder.Services.AddHostedService<BalanceWatcher>();

        builder.Services.AddControllers()
            .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new InternalControllerFeatureProvider()))
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var message = context.ModelState.Values
                    .SelectMany(entry => entry.Errors)
                    .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Invalid request." : error.ErrorMessage)
                    .FirstOrDefault() ?? "Invalid request.";
                return new BadRequestObjectResult(new ErrorResponse(message));
            };
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseExceptionHandler(handler =>
        {
            handler.Run(async context =>
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SolanaWalletService");
                var options = context.RequestServices.GetRequiredService<AppOptions>();
                var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                if (error is not null)
                    logger.LogError("Unhandled request error: {Message}", SecretRedactor.Redact(error.Message, options));

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new ErrorResponse("Internal server error."), context.RequestAborted);
            });
        });
        app.UseMiddleware<ApiKeyMiddleware>();
        app.MapControllers();
        app.MapGet("/health", () => Results.Json(new HealthResponse("ok")));

        try
        {
            await PayoutDatabase.InitializeAsync(app.Services, appOptions, app.Environment.ContentRootPath, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(
                "PostgreSQL is required and could not be initialized: {Message}",
                SecretRedactor.Redact(ex.Message, appOptions));
            throw;
        }

        app.Logger.LogInformation(
            "SOL wallet API listening on http://127.0.0.1:5101 ({Network}, {Address}, telegramBot: {TelegramBot}).",
            appOptions.Network,
            SolAddress.Shorten(appOptions.WalletAddress),
            appOptions.TelegramBotToken is not null ? "on" : "off");

        app.Run();
    }
}
