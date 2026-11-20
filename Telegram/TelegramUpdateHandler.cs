using System.Globalization;
using SolanaWalletService.Configuration;
using SolanaWalletService.Payouts;
using SolanaWalletService.Solana;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace SolanaWalletService.Telegram;

internal sealed class TelegramUpdateHandler
{
    private const int MaxOffset = 1_000_000;

    private readonly Services.SolanaWalletService _wallet;
    private readonly PayoutService _payouts;
    private readonly AppOptions _options;
    private readonly ILogger<TelegramUpdateHandler> _logger;
    private readonly object _withdrawLock = new();
    private WithdrawDraft? _withdraw;

    public TelegramUpdateHandler(
        Services.SolanaWalletService wallet,
        PayoutService payouts,
        AppOptions options,
        ILogger<TelegramUpdateHandler> logger)
    {
        _wallet = wallet;
        _payouts = payouts;
        _options = options;
        _logger = logger;
    }

    public async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(bot, callback, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (update.Message is { } message)
            await HandleMessageAsync(bot, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken cancellationToken)
    {
        if (message.From?.IsBot == true)
            return;

        var command = Command(message.Text);
        if (message.Chat.Type != ChatType.Private)
        {
            if (command == "/start")
                await SendPlainAsync(bot, message.Chat.Id, "Ping me in a private chat 👋", cancellationToken).ConfigureAwait(false);

            return;
        }

        if (message.From is null || message.From.Id != _options.TelegramAdminId)
        {
            _logger.LogInformation("Rejected Telegram user {UserId}.", message.From?.Id);
            await SendPlainAsync(bot, message.Chat.Id, "This bot is private.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command is not null)
            ClearWithdraw();
        else if (InWithdraw())
        {
            await ApplyWithdrawTextAsync(bot, message, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (command)
        {
            case "/address":
                await ShowAddressAsync(bot, message.Chat.Id, messageId: null, cancellationToken).ConfigureAwait(false);
                break;
            case "/balance":
                await ShowBalanceAsync(bot, message.Chat.Id, messageId: null, cancellationToken).ConfigureAwait(false);
                break;
            case "/transactions":
                await ShowTransactionsAsync(bot, message.Chat.Id, messageId: null, offset: 0, cancellationToken).ConfigureAwait(false);
                break;
            case "/payouts":
                await ShowPayoutsAsync(bot, message.Chat.Id, messageId: null, offset: 0, cancellationToken).ConfigureAwait(false);
                break;
            case "/settings":
                await ShowSettingsAsync(bot, message.Chat.Id, messageId: null, notice: null, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await ShowMenuAsync(bot, message.Chat.Id, messageId: null, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken cancellationToken)
    {
        var message = callback.Message;
        if (message is null)
        {
            await AnswerAsync(bot, callback, "Open a private chat and send /start.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.Chat.Type != ChatType.Private || callback.From.Id != _options.TelegramAdminId)
        {
            _logger.LogInformation("Rejected Telegram callback from user {UserId}.", callback.From.Id);
            await AnswerAsync(bot, callback, "Not allowed.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryReadCallback(callback.Data, out var action, out var offset))
        {
            _logger.LogInformation("Ignored an invalid Telegram callback.");
            await AnswerAsync(bot, callback, "That button doesn't work anymore.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (action == "wa" && !WithdrawIs(WithdrawStep.Review))
        {
            await AnswerAsync(bot, callback, "That step is over.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (action == "ws" && !WithdrawIs(WithdrawStep.Memo))
        {
            await AnswerAsync(bot, callback, "That step is over.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await AnswerAsync(bot, callback, text: null, cancellationToken).ConfigureAwait(false);
        if (action is not ("wd" or "wx" or "ws" or "wa"))
            ClearWithdraw();

        switch (action)
        {
            case "addr":
                await ShowAddressAsync(bot, message.Chat.Id, message.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "bal":
                await ShowBalanceAsync(bot, message.Chat.Id, message.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "tx":
                await ShowTransactionsAsync(bot, message.Chat.Id, message.Id, offset, cancellationToken).ConfigureAwait(false);
                break;
            case "po":
                await ShowPayoutsAsync(bot, message.Chat.Id, message.Id, offset, cancellationToken).ConfigureAwait(false);
                break;
            case "ps":
                await ShowSettingsAsync(bot, message.Chat.Id, message.Id, notice: null, cancellationToken).ConfigureAwait(false);
                break;
            case "pp":
                await ShowAsync(bot, message.Chat.Id, message.Id, _ => Task.FromResult(TelegramScreens.ConfirmPause()), cancellationToken).ConfigureAwait(false);
                break;
            case "py":
                await ShowSettingsAsync(bot, message.Chat.Id, message.Id, await _payouts.PauseAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                break;
            case "pr":
                await ShowAsync(bot, message.Chat.Id, message.Id, _ => Task.FromResult(TelegramScreens.ConfirmResume()), cancellationToken).ConfigureAwait(false);
                break;
            case "ry":
                await ShowSettingsAsync(bot, message.Chat.Id, message.Id, await _payouts.ResumeAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                break;
            case "pn":
                await ShowSettingsAsync(bot, message.Chat.Id, message.Id, await _payouts.ToggleNotificationsAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                break;
            case "wd":
                await StartWithdrawAsync(bot, message.Chat.Id, message.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "wx":
                ClearWithdraw();
                await ShowMenuAsync(bot, message.Chat.Id, message.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "ws":
                await SkipMemoAsync(bot, message.Chat.Id, message.Id, cancellationToken).ConfigureAwait(false);
                break;
            case "wa":
                await ApproveWithdrawAsync(bot, message.Chat.Id, message.Id, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await ShowMenuAsync(bot, message.Chat.Id, message.Id, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task ShowMenuAsync(ITelegramBotClient bot, long chatId, int? messageId, CancellationToken cancellationToken)
    {
        await ShowAsync(bot, chatId, messageId, async ct =>
        {
            try
            {
                var balance = await _wallet.GetBalanceAsync(ct).ConfigureAwait(false);
                return TelegramScreens.Menu(_options, balance);
            }
            catch (SolanaRpcException ex)
            {
                _logger.LogWarning("Menu refresh failed ({Kind}).", ex.Kind);
                return TelegramScreens.MenuUnavailable(_options, ex.Message);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task ShowAddressAsync(ITelegramBotClient bot, long chatId, int? messageId, CancellationToken cancellationToken)
    {
        return ShowAsync(bot, chatId, messageId, async _ =>
        {
            var address = await _wallet.GetAddressAsync().ConfigureAwait(false);
            return TelegramScreens.Address(address);
        }, cancellationToken);
    }

    private Task ShowBalanceAsync(ITelegramBotClient bot, long chatId, int? messageId, CancellationToken cancellationToken)
    {
        return ShowAsync(bot, chatId, messageId, async ct =>
        {
            var balance = await _wallet.GetBalanceAsync(ct).ConfigureAwait(false);
            return TelegramScreens.Balance(balance);
        }, cancellationToken);
    }

    private Task ShowPayoutsAsync(ITelegramBotClient bot, long chatId, int? messageId, int offset, CancellationToken cancellationToken)
    {
        return ShowAsync(bot, chatId, messageId, async ct =>
        {
            var page = await _payouts.ListAsync(PayoutLimits.TelegramPageSize, offset, ct).ConfigureAwait(false);
            return TelegramScreens.Payouts(page);
        }, cancellationToken);
    }

    private Task ShowSettingsAsync(ITelegramBotClient bot, long chatId, int? messageId, string? notice, CancellationToken cancellationToken)
    {
        return ShowAsync(bot, chatId, messageId, async ct => TelegramScreens.PayoutSettings(await _payouts.SnapshotAsync(ct).ConfigureAwait(false), notice), cancellationToken);
    }

    private Task ShowTransactionsAsync(ITelegramBotClient bot, long chatId, int? messageId, int offset, CancellationToken cancellationToken)
    {
        return ShowAsync(bot, chatId, messageId, async ct =>
        {
            var page = await _wallet.GetTransactionsAsync(Services.SolanaWalletService.TelegramPageSize, offset, ct).ConfigureAwait(false);
            return TelegramScreens.Transactions(page);
        }, cancellationToken);
    }

    private async Task StartWithdrawAsync(ITelegramBotClient bot, long chatId, int messageId, CancellationToken cancellationToken)
    {
        var snapshot = await _payouts.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.SigningConfigured)
        {
            ClearWithdraw();
            var problem = snapshot.SigningProblem ?? "Can't sign from this wallet yet.";
            await ShowAsync(bot, chatId, messageId, _ => Task.FromResult(TelegramScreens.Problem(problem)), cancellationToken).ConfigureAwait(false);
            return;
        }

        lock (_withdrawLock)
            _withdraw = new WithdrawDraft { PromptMessageId = messageId };

        await ShowWithdrawAsync(bot, chatId, messageId, problem: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyWithdrawTextAsync(ITelegramBotClient bot, Message message, CancellationToken cancellationToken)
    {
        int? promptId;
        WithdrawStep step;
        lock (_withdrawLock)
        {
            if (_withdraw is null)
            {
                promptId = null;
                step = WithdrawStep.Address;
            }
            else
            {
                promptId = _withdraw.PromptMessageId;
                step = _withdraw.Step;
            }
        }

        if (!InWithdraw())
        {
            await ShowMenuAsync(bot, message.Chat.Id, promptId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = message.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            await ShowWithdrawAsync(bot, message.Chat.Id, promptId, "Send it as text.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (text.Length > 500)
        {
            await ShowWithdrawAsync(bot, message.Chat.Id, promptId, "That's too long.", cancellationToken).ConfigureAwait(false);
            return;
        }

        string? problem = null;
        switch (step)
        {
            case WithdrawStep.Address:
                if (!PayoutRequests.TryDestination(text, _options, out var destination, out problem))
                    break;

                lock (_withdrawLock)
                {
                    if (_withdraw is { Step: WithdrawStep.Address })
                    {
                        _withdraw.Address = destination.Address;
                        _withdraw.Step = WithdrawStep.Amount;
                    }
                }

                break;
            case WithdrawStep.Amount:
                if (!PayoutRequests.TryAmount(text, _options, out var lamports, out problem))
                    break;

                var formatted = SolAmount.FormatSol(lamports);
                lock (_withdrawLock)
                {
                    if (_withdraw is { Step: WithdrawStep.Amount })
                    {
                        _withdraw.Amount = formatted;
                        _withdraw.Step = WithdrawStep.Memo;
                    }
                }

                break;
            case WithdrawStep.Memo:
                if (!PayoutRequests.TryComment(text, out var memo, out problem))
                {
                    problem = MemoProblem(problem);
                    break;
                }

                if (memo is null)
                {
                    problem = "Send a memo, or tap Skip.";
                    break;
                }

                lock (_withdrawLock)
                {
                    if (_withdraw is { Step: WithdrawStep.Memo })
                    {
                        _withdraw.Memo = memo;
                        _withdraw.Step = WithdrawStep.Review;
                    }
                }

                break;
            default:
                problem = "Tap Approve or Cancel.";
                break;
        }

        await ShowWithdrawAsync(bot, message.Chat.Id, promptId, problem, cancellationToken).ConfigureAwait(false);
    }

    private Task SkipMemoAsync(ITelegramBotClient bot, long chatId, int messageId, CancellationToken cancellationToken)
    {
        lock (_withdrawLock)
        {
            if (_withdraw is { Step: WithdrawStep.Memo })
            {
                _withdraw.Memo = null;
                _withdraw.Step = WithdrawStep.Review;
            }
        }

        return ShowWithdrawAsync(bot, chatId, messageId, problem: null, cancellationToken);
    }

    private async Task ApproveWithdrawAsync(ITelegramBotClient bot, long chatId, int messageId, CancellationToken cancellationToken)
    {
        string address;
        string amount;
        string? memo;
        lock (_withdrawLock)
        {
            if (_withdraw is not { Step: WithdrawStep.Review } draft
                || draft.Address.Length == 0
                || draft.Amount.Length == 0)
            {
                address = "";
                amount = "";
                memo = null;
            }
            else
            {
                address = draft.Address;
                amount = draft.Amount;
                memo = draft.Memo;
            }
        }

        if (address.Length == 0)
        {
            await ShowWithdrawAsync(bot, chatId, messageId, problem: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var command = new PayoutCommand(Guid.NewGuid().ToString("D"), address, amount, memo);
        var result = await _payouts.CreateAsync(command, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case PayoutResult.Ok:
                lock (_withdrawLock)
                {
                    if (_withdraw is { Step: WithdrawStep.Review } current
                        && current.Address == address
                        && current.Amount == amount
                        && current.Memo == memo)
                    {
                        _withdraw = null;
                    }
                }

                var snapshot = await _payouts.SnapshotAsync(cancellationToken).ConfigureAwait(false);
                await ShowAsync(
                    bot,
                    chatId,
                    messageId,
                    _ => Task.FromResult(TelegramScreens.WithdrawQueued(amount, address, memo, snapshot.Paused, snapshot.NeedsReview > 0)),
                    cancellationToken).ConfigureAwait(false);
                return;
            case PayoutResult.Error failure:
                await ShowWithdrawAsync(bot, chatId, messageId, failure.Message, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await ShowWithdrawAsync(bot, chatId, messageId, "Couldn't queue that withdraw.", cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task ShowWithdrawAsync(
        ITelegramBotClient bot,
        long chatId,
        int? messageId,
        string? problem,
        CancellationToken cancellationToken)
    {
        WithdrawStep step;
        string address;
        string amount;
        string? memo;
        lock (_withdrawLock)
        {
            if (_withdraw is null)
            {
                step = WithdrawStep.Address;
                address = "";
                amount = "";
                memo = null;
            }
            else
            {
                step = _withdraw.Step;
                address = _withdraw.Address;
                amount = _withdraw.Amount;
                memo = _withdraw.Memo;
                messageId ??= _withdraw.PromptMessageId;
            }
        }

        if (!InWithdraw())
        {
            await ShowMenuAsync(bot, chatId, messageId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var snapshot = step == WithdrawStep.Review
            ? await _payouts.SnapshotAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var shown = await ShowAsync(bot, chatId, messageId, async ct =>
        {
            switch (step)
            {
                case WithdrawStep.Address:
                    return TelegramScreens.WithdrawAddress(_options.NetworkLabel, problem);
                case WithdrawStep.Amount:
                    return TelegramScreens.WithdrawAmount(address, await TryBalanceTextAsync(ct).ConfigureAwait(false), MaxPayoutText(), problem);
                case WithdrawStep.Memo:
                    return TelegramScreens.WithdrawMemo(address, amount, problem);
                default:
                    return TelegramScreens.WithdrawReview(
                        address,
                        amount,
                        memo,
                        snapshot?.Paused == true,
                        snapshot?.NeedsReview > 0,
                        problem);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (shown is int promptId)
        {
            lock (_withdrawLock)
            {
                if (_withdraw is not null)
                    _withdraw.PromptMessageId = promptId;
            }
        }
    }

    private async Task<string?> TryBalanceTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            var balance = await _wallet.GetBalanceAsync(cancellationToken).ConfigureAwait(false);
            return balance.Balance;
        }
        catch (SolanaRpcException ex)
        {
            _logger.LogWarning("Withdraw balance check failed ({Kind}).", ex.Kind);
            return null;
        }
    }

    private string MaxPayoutText()
    {
        return SolAmount.FormatSol(_options.MaxPayoutLamports);
    }

    private bool InWithdraw()
    {
        lock (_withdrawLock)
            return _withdraw is not null;
    }

    private bool WithdrawIs(WithdrawStep step)
    {
        lock (_withdrawLock)
            return _withdraw?.Step == step;
    }

    private void ClearWithdraw()
    {
        lock (_withdrawLock)
            _withdraw = null;
    }

    private static string MemoProblem(string error)
    {
        return error switch
        {
            "Comment must be 123 bytes or less." => "Memo must be 123 bytes or less.",
            "Comment is not valid text." => "Memo isn't valid text.",
            _ => error
        };
    }

    private async Task<int?> ShowAsync(
        ITelegramBotClient bot,
        long chatId,
        int? messageId,
        Func<CancellationToken, Task<(string Text, InlineKeyboardMarkup Keyboard)>> screen,
        CancellationToken cancellationToken)
    {
        (string Text, InlineKeyboardMarkup Keyboard) view;
        try
        {
            view = await screen(cancellationToken).ConfigureAwait(false);
        }
        catch (SolanaRpcException ex)
        {
            _logger.LogWarning("Telegram screen failed ({Kind}).", ex.Kind);
            view = TelegramScreens.Problem(ex.Message);
        }

        if (messageId is int existingMessageId)
        {
            try
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: existingMessageId,
                    text: view.Text,
                    parseMode: ParseMode.Html,
                    replyMarkup: view.Keyboard,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return existingMessageId;
            }
            catch (ApiRequestException ex) when (IsNotModified(ex))
            {
                return existingMessageId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Could not edit Telegram message {MessageId}: {Message}",
                    existingMessageId,
                    SecretRedactor.Redact(ex.Message, _options));
            }
        }

        try
        {
            var sent = await bot.SendMessage(
                chatId: chatId,
                text: view.Text,
                parseMode: ParseMode.Html,
                replyMarkup: view.Keyboard,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return sent.MessageId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not send Telegram message: {Message}", SecretRedactor.Redact(ex.Message, _options));
            return null;
        }
    }

    private async Task SendPlainAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await bot.SendMessage(chatId, text, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not send Telegram message: {Message}", SecretRedactor.Redact(ex.Message, _options));
        }
    }

    private async Task AnswerAsync(ITelegramBotClient bot, CallbackQuery callback, string? text, CancellationToken cancellationToken)
    {
        try
        {
            await bot.AnswerCallbackQuery(callback.Id, text, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Could not answer a Telegram callback: {Message}", SecretRedactor.Redact(ex.Message, _options));
        }
    }

    private static bool TryReadCallback(string? data, out string action, out int offset)
    {
        action = string.Empty;
        offset = 0;
        if (string.IsNullOrEmpty(data) || data.Length > 32 || !data.StartsWith("sw:", StringComparison.Ordinal))
            return false;

        var parts = data.Split(':');
        if (parts.Length is < 2 or > 3 || parts[0] != "sw")
            return false;

        action = parts[1];
        if (action is "addr" or "bal" or "refresh" or "menu" or "ps" or "pp" or "py" or "pr" or "ry" or "pn"
            or "wd" or "wx" or "ws" or "wa")
            return parts.Length == 2;

        if (action is not ("tx" or "po") || parts.Length != 3)
            return false;

        return int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            && offset is >= 0 and <= MaxOffset;
    }

    private static string? Command(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('/'))
            return null;

        var token = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var at = token.IndexOf('@');
        if (at >= 0)
            token = token[..at];

        return token.ToLowerInvariant();
    }

    private static bool IsNotModified(ApiRequestException exception)
    {
        return exception.ErrorCode == 400
            && exception.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);
    }

    private enum WithdrawStep
    {
        Address,
        Amount,
        Memo,
        Review
    }

    private sealed class WithdrawDraft
    {
        public WithdrawStep Step { get; set; } = WithdrawStep.Address;

        public int? PromptMessageId { get; set; }

        public string Address { get; set; } = "";

        public string Amount { get; set; } = "";

        public string? Memo { get; set; }
    }
}
