using System.Text;
using SolanaWalletService.Configuration;
using SolanaWalletService.Services;
using SolanaWalletService.Solana;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SolanaWalletService.Payouts;

internal sealed class TelegramPayoutNotifier : IPayoutNotifier
{
    private readonly ITelegramBotClient _bot;
    private readonly SolUsdtQuote _quotes;
    private readonly AppOptions _options;
    private readonly ILogger<TelegramPayoutNotifier> _logger;

    public TelegramPayoutNotifier(
        ITelegramBotClient bot,
        SolUsdtQuote quotes,
        AppOptions options,
        ILogger<TelegramPayoutNotifier> logger)
    {
        _bot = bot;
        _quotes = quotes;
        _options = options;
        _logger = logger;
    }

    public async Task NotifyAsync(PayoutNotice notice, CancellationToken cancellationToken)
    {
        var price = await _quotes.TryGetAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync(Text(notice, SolUsdtQuote.Format(notice.Amount, price)), cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyDepositAsync(DepositNotice notice, CancellationToken cancellationToken)
    {
        var price = await _quotes.TryGetAsync(cancellationToken).ConfigureAwait(false);
        await SendAsync(TopUp(notice, price), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await _bot.SendMessage(
                chatId: _options.TelegramAdminId,
                text: text,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Couldn't send a payout notice ({Type}).", ex.GetType().Name);
        }
    }

    private static string Text(PayoutNotice notice, string? usdt)
    {
        var amount = Esc(notice.Amount ?? "?");
        var destination = Esc(notice.Destination is null ? "?" : SolAddress.Shorten(notice.Destination));
        var detail = string.IsNullOrWhiteSpace(notice.Detail) ? null : Esc(notice.Detail);
        var transfer = Transfer(amount, usdt, destination);
        return notice.Kind switch
        {
            PayoutNoticeKind.Confirmed => Confirmed(transfer, notice.ExplorerUrl),
            PayoutNoticeKind.Failed => "❌ <b>Payout failed</b>\n\n" + transfer
                + (detail is null ? "" : "\n" + detail),
            PayoutNoticeKind.NeedsReview => "👀 <b>A payout needs a look</b>\n\n" + transfer + "\n"
                + (detail ?? "I couldn't prove it landed, so I did not send it again."),
            PayoutNoticeKind.InsufficientBalance => "⚠️ <b>Not enough SOL</b>\n\n" + transfer + "\nIt stays queued until the wallet can cover it.",
            PayoutNoticeKind.Queued => Queued(transfer, detail),
            _ => "⚠️ Solana RPC has been unreachable for a few minutes. Payouts are waiting."
        };
    }

    private static string Queued(string transfer, string? comment)
    {
        var text = "⏳ <b>Payout queued</b>\n\n" + transfer;
        if (comment is not null)
            text += "\n<i>" + comment + "</i>";

        return text;
    }

    private static string TopUp(DepositNotice notice, decimal? price)
    {
        var text = new StringBuilder()
            .AppendLine("💰 <b>Balance topped up</b>")
            .AppendLine();

        foreach (var credit in notice.Credits)
        {
            text.Append('+').Append(Esc(credit.Amount)).Append(" SOL");
            AppendUsdt(text, SolUsdtQuote.Format(credit.Amount, price));
            if (!string.IsNullOrWhiteSpace(credit.From))
                text.Append(" from <code>").Append(Esc(SolAddress.Shorten(credit.From))).Append("</code>");

            text.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(notice.Balance))
        {
            text.Append("Balance is <b>").Append(Esc(notice.Balance)).Append(" SOL</b> now");
            var balanceUsdt = SolUsdtQuote.Format(notice.Balance, price);
            if (!string.IsNullOrWhiteSpace(balanceUsdt))
                text.Append("\n≈ <b>").Append(Esc(balanceUsdt)).Append("</b> USDT");

            text.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(notice.ExplorerUrl))
            text.Append("<a href=\"").Append(Attr(notice.ExplorerUrl)).Append("\">Solana Explorer</a>");

        return text.ToString().TrimEnd();
    }

    private static string Confirmed(string transfer, string? explorerUrl)
    {
        var text = "✅ <b>Payout sent</b>\n\n" + transfer;
        if (!string.IsNullOrWhiteSpace(explorerUrl))
            text += "\n<a href=\"" + Attr(explorerUrl) + "\">Solana Explorer</a>";

        return text;
    }

    private static string Transfer(string amount, string? usdt, string destination)
    {
        var text = amount + " SOL → <code>" + destination + "</code>";
        if (string.IsNullOrWhiteSpace(usdt))
            return text;

        return text + "\n≈ <b>" + Esc(usdt) + "</b> USDT";
    }

    private static void AppendUsdt(StringBuilder text, string? usdt)
    {
        if (!string.IsNullOrWhiteSpace(usdt))
            text.Append(" ≈ <b>").Append(Esc(usdt)).Append("</b> USDT");
    }

    private static string Esc(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string Attr(string value)
    {
        return Esc(value).Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}

internal sealed class LogPayoutNotifier : IPayoutNotifier
{
    private readonly ILogger<LogPayoutNotifier> _logger;

    public LogPayoutNotifier(ILogger<LogPayoutNotifier> logger)
    {
        _logger = logger;
    }

    public Task NotifyAsync(PayoutNotice notice, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Payout notice {Kind} for {PayoutId}.", notice.Kind, notice.PayoutId);
        return Task.CompletedTask;
    }

    public Task NotifyDepositAsync(DepositNotice notice, CancellationToken cancellationToken)
    {
        var amount = notice.Credits.Count == 0 ? "?" : notice.Credits[0].Amount;
        _logger.LogInformation("Deposit notice for {Amount} SOL.", amount);
        return Task.CompletedTask;
    }
}
