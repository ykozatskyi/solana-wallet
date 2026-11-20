using SolanaWalletService.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace SolanaWalletService.Telegram;

internal sealed class TelegramBotWorker : BackgroundService
{
    private static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery];

    private readonly ITelegramBotClient _bot;
    private readonly TelegramUpdateHandler _handler;
    private readonly AppOptions _options;
    private readonly ILogger<TelegramBotWorker> _logger;

    public TelegramBotWorker(
        ITelegramBotClient bot,
        TelegramUpdateHandler handler,
        AppOptions options,
        ILogger<TelegramBotWorker> logger)
    {
        _bot = bot;
        _handler = handler;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation("Telegram long polling is starting.");

        try
        {
            await _bot.DeleteWebhook(dropPendingUpdates: false, cancellationToken: stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not delete the Telegram webhook: {Message}", SecretRedactor.Redact(ex.Message, _options));
        }

        var offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdates(offset, 50, 30, AllowedUpdates, stoppingToken).ConfigureAwait(false);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    try
                    {
                        await _handler.HandleAsync(_bot, update, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            "Telegram update {UpdateId} failed: {Message}",
                            update.Id,
                            SecretRedactor.Redact(ex.Message, _options));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Telegram polling failed: {Message}", SecretRedactor.Redact(ex.Message, _options));
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Telegram long polling stopped.");
    }
}
