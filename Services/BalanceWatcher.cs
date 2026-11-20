using SolanaWalletService.Configuration;
using SolanaWalletService.Models;
using SolanaWalletService.Payouts;
using SolanaWalletService.Solana;

namespace SolanaWalletService.Services;

internal sealed class BalanceWatcher : BackgroundService
{
    private const int PageSize = 20;
    private const int MaxPages = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly SolanaWalletService _wallet;
    private readonly DepositStore _deposits;
    private readonly IPayoutNotifier _notifier;
    private readonly AppOptions _options;
    private readonly ILogger<BalanceWatcher> _logger;

    public BalanceWatcher(
        SolanaWalletService wallet,
        DepositStore deposits,
        IPayoutNotifier notifier,
        AppOptions options,
        ILogger<BalanceWatcher> logger)
    {
        _wallet = wallet;
        _deposits = deposits;
        _notifier = notifier;
        _options = options;
        _logger = logger;
    }

    public static void SelfCheck()
    {
        var skipped = Credits(Sample(aborted: true, Incoming("1", "from")));
        var outgoing = Credits(Sample(aborted: false, new WalletMessageDto(MessageDirections.Outgoing, "1", "1000000000", "to", "transfer", false)));
        var toppedUp = Credits(Sample(aborted: false, Incoming("0.025", "from")));
        if (skipped.Count != 0
            || outgoing.Count != 0
            || toppedUp.Count != 1
            || toppedUp[0].Amount != "0.025"
            || toppedUp[0].From != "from")
        {
            throw new InvalidOperationException("Deposit scan self-check failed.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation("Balance watcher started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SolanaRpcException ex)
                {
                    _logger.LogWarning("Balance check failed ({Kind}).", ex.Kind);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Balance watcher failed ({Type}).", ex.GetType().Name);
                }

                try
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            _logger.LogInformation("Balance watcher stopped.");
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var cursor = await _deposits.GetCursorAsync(cancellationToken).ConfigureAwait(false);
        if (cursor is null)
        {
            var latest = await _wallet.GetRecentSignaturesAsync(1, null, "finalized", cancellationToken).ConfigureAwait(false);
            var signature = latest.Count == 0 ? null : latest[0].Signature;
            var slot = latest.Count == 0 ? 0 : latest[0].Slot;
            await _deposits.TryArmAsync(signature, slot, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Balance watcher armed. Older transfers stay quiet.");
            return;
        }

        var fresh = new List<SolanaSignatureInfo>();
        var reachedCursor = cursor.Signature is null;
        string? before = null;
        for (var pageIndex = 0; pageIndex < MaxPages; pageIndex++)
        {
            var page = await _wallet.GetRecentSignaturesAsync(PageSize, before, "finalized", cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                reachedCursor = true;
                break;
            }

            foreach (var signature in page)
            {
                if (cursor.Signature is not null
                    && string.Equals(signature.Signature, cursor.Signature, StringComparison.Ordinal))
                {
                    reachedCursor = true;
                    break;
                }

                fresh.Add(signature);
            }

            if (reachedCursor || page.Count < PageSize)
            {
                reachedCursor = true;
                break;
            }

            before = page[^1].Signature;
        }

        if (fresh.Count == 0)
            return;

        if (!reachedCursor)
            _logger.LogWarning("Balance watcher missed older top-ups after a long gap. New ones from here are still announced.");

        var newest = fresh[0];
        fresh.Reverse();
        string? balance = null;
        foreach (var signature in fresh)
        {
            if (signature.Failed)
                continue;

            var transaction = await _wallet.GetChainTransactionAsync(signature.Signature, "finalized", cancellationToken).ConfigureAwait(false);
            if (transaction is null || transaction.Failed)
                continue;

            var dto = SolanaWalletService.ToDto(transaction, _options.TransactionExplorerUrl);
            var credits = Credits(dto);
            if (credits.Count == 0)
                continue;

            var amount = credits.Sum(credit => ParseLamports(credit.Amount));
            var from = credits.Count == 1 ? credits[0].From : null;
            var recorded = await _deposits.TryRecordAsync(
                signature.Signature,
                signature.Slot,
                amount,
                from,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            if (!recorded)
                continue;

            balance ??= await TryBalanceAsync(cancellationToken).ConfigureAwait(false);
            await _notifier.NotifyDepositAsync(
                new DepositNotice(credits, balance, dto.ExplorerUrl),
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Wallet topped up by {Amount} SOL.",
                string.Join(", ", credits.Select(credit => credit.Amount)));
        }

        await _deposits.AdvanceAsync(newest.Signature, newest.Slot, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryBalanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var balance = await _wallet.GetBalanceAsync(cancellationToken).ConfigureAwait(false);
            return balance.Balance;
        }
        catch (SolanaRpcException ex)
        {
            _logger.LogWarning("Balance after a top-up could not be read ({Kind}).", ex.Kind);
            return null;
        }
    }

    private static long ParseLamports(string amount)
    {
        return SolAmount.TryParseSol(amount, out var lamports) ? lamports : 0;
    }

    private static IReadOnlyList<DepositCredit> Credits(WalletTransactionDto transaction)
    {
        if (transaction.Aborted == true)
            return [];

        var credits = new List<DepositCredit>();
        foreach (var message in transaction.Messages)
        {
            if (message.Direction != MessageDirections.Incoming || message.Bounced || message.Amount is null)
                continue;

            credits.Add(new DepositCredit(message.Amount, message.Counterparty));
        }

        return credits;
    }

    private static WalletTransactionDto Sample(bool? aborted, params WalletMessageDto[] messages)
    {
        return new WalletTransactionDto("hash", "1", null, null, null, aborted, null, null, "https://explorer.solana.com/tx/hash", messages);
    }

    private static WalletMessageDto Incoming(string amount, string from)
    {
        return new WalletMessageDto(MessageDirections.Incoming, amount, "25000000", from, "transfer", false);
    }
}
