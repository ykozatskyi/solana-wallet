using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SolanaWalletService.Configuration;

namespace SolanaWalletService.Persistence;

internal static class PayoutDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task InitializeAsync(
        IServiceProvider services,
        AppOptions options,
        string contentRoot,
        CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SolanaWalletService.Persistence");
        var factory = services.GetRequiredService<IDbContextFactory<WalletDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Applying PostgreSQL migrations for SOLWalletDB.");
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSettingsAsync(db, options, contentRoot, logger, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("PostgreSQL payout store is ready.");
    }

    private static async Task EnsureSettingsAsync(
        WalletDbContext db,
        AppOptions options,
        string contentRoot,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (await db.PayoutSettings.AnyAsync(cancellationToken).ConfigureAwait(false))
            return;

        var (paused, notify, imported) = ReadInitialSettings(contentRoot, options.PayoutsEnabled, logger);
        db.PayoutSettings.Add(new PayoutSettingsEntity
        {
            Id = 1,
            Paused = paused,
            NotifyOnSuccess = notify
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return;
        }

        logger.LogInformation(
            imported
                ? "Imported payout settings into PostgreSQL (paused: {Paused}, success alerts: {Alerts})."
                : "Created payout settings in PostgreSQL (paused: {Paused}, success alerts: {Alerts}).",
            paused,
            notify);
    }

    private static (bool Paused, bool NotifyOnSuccess, bool Imported) ReadInitialSettings(
        string contentRoot,
        bool payoutsEnabledByDefault,
        ILogger logger)
    {
        var paused = !payoutsEnabledByDefault;
        var notify = true;
        var path = Path.Combine(contentRoot, "data", "payout-settings.json");
        if (!File.Exists(path))
            return (paused, notify, false);

        try
        {
            var file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), JsonOptions);
            if (file is null)
                return (paused, notify, false);

            return (file.Paused, file.NotifyOnSuccess, true);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Payout settings JSON could not be imported ({Type}). Using the configured default.",
                ex.GetType().Name);
            return (paused, notify, false);
        }
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation)
                return true;
        }

        return false;
    }

    private sealed class SettingsFile
    {
        public bool Paused { get; set; }

        public bool NotifyOnSuccess { get; set; } = true;
    }
}
