using System.Globalization;
using SolanaWalletService.Configuration;
using SolanaWalletService.Models;
using SolanaWalletService.Solana;

namespace SolanaWalletService.Payouts;

internal sealed class PayoutService
{
    private readonly PayoutStore _store;
    private readonly Services.SolanaWalletService _wallet;
    private readonly WalletSigningGate _gate;
    private readonly AppOptions _options;
    private readonly TimeProvider _time;
    private readonly IPayoutNotifier _notifier;
    private readonly ILogger<PayoutService> _logger;
    private readonly SemaphoreSlim _create = new(1, 1);

    public PayoutService(
        PayoutStore store,
        Services.SolanaWalletService wallet,
        WalletSigningGate gate,
        AppOptions options,
        TimeProvider time,
        IPayoutNotifier notifier,
        ILogger<PayoutService> logger)
    {
        _store = store;
        _wallet = wallet;
        _gate = gate;
        _options = options;
        _time = time;
        _notifier = notifier;
        _logger = logger;
    }

    internal async Task<PayoutResult> CreateAsync(PayoutCommand command, CancellationToken cancellationToken)
    {
        if (!_gate.CanQueue)
            return new PayoutResult.Error(StatusCodes.Status503ServiceUnavailable, _gate.Problem ?? "Payout signing is not configured.");

        if (!PayoutRequests.TryNormalize(command, _options, out var payout, out var error))
            return new PayoutResult.Error(StatusCodes.Status400BadRequest, error);

        PayoutRecord? queued = null;
        await _create.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _store.FindByRequestAsync(payout.RequestId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return PayoutRequests.SameDetails(existing, payout)
                    ? new PayoutResult.Ok(existing, false)
                    : new PayoutResult.Error(StatusCodes.Status409Conflict, "requestId was already used for a different payout.");
            }

            long balanceLamports;
            try
            {
                var balance = await _wallet.GetBalanceAsync(cancellationToken).ConfigureAwait(false);
                if (!long.TryParse(balance.BalanceNano, NumberStyles.None, CultureInfo.InvariantCulture, out balanceLamports)
                    || balanceLamports < 0)
                {
                    return new PayoutResult.Error(StatusCodes.Status502BadGateway, "Wallet balance was unreadable.");
                }
            }
            catch (SolanaRpcException ex)
            {
                _logger.LogWarning("Payout balance check failed ({Kind}).", ex.Kind);
                return new PayoutResult.Error(MapStatus(ex.Kind), ex.Message);
            }

            var need = payout.AmountLamports + _options.FeeReserveLamports;
            var now = _time.GetUtcNow();
            var record = new PayoutRecord
            {
                Id = Guid.NewGuid(),
                RequestId = payout.RequestId,
                DestinationRaw = payout.Destination.Address,
                DestinationFriendly = payout.Destination.Address,
                AmountLamports = payout.AmountLamports,
                Comment = payout.Comment,
                Status = PayoutStatus.Queued,
                FeeReserveLamports = _options.FeeReserveLamports,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            var added = await _store.TryAddIfAffordableAsync(
                record,
                reserved => CanAfford(balanceLamports, reserved, need),
                cancellationToken).ConfigureAwait(false);
            switch (added)
            {
                case PayoutAddResult.Added:
                    _logger.LogInformation(
                        "Queued payout {PayoutId} for {Amount} SOL to {Address}.",
                        record.Id,
                        SolAmount.FormatSol(record.AmountLamports),
                        SolAddress.Shorten(record.DestinationFriendly));
                    queued = record.Clone();
                    break;
                case PayoutAddResult.Insufficient:
                    return new PayoutResult.Error(StatusCodes.Status409Conflict, "Not enough SOL is available for this payout.");
                default:
                    existing = await _store.FindByRequestAsync(payout.RequestId, cancellationToken).ConfigureAwait(false);
                    if (existing is null)
                        return new PayoutResult.Error(StatusCodes.Status409Conflict, "requestId was already used for a different payout.");

                    return PayoutRequests.SameDetails(existing, payout)
                        ? new PayoutResult.Ok(existing, false)
                        : new PayoutResult.Error(StatusCodes.Status409Conflict, "requestId was already used for a different payout.");
            }
        }
        finally
        {
            _create.Release();
        }

        if (queued is null)
            return new PayoutResult.Error(StatusCodes.Status500InternalServerError, "Internal server error.");

        await NotifyQueuedAsync(queued, cancellationToken).ConfigureAwait(false);
        return new PayoutResult.Ok(queued, true);
    }

    private async Task NotifyQueuedAsync(PayoutRecord payout, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _notifier.NotifyAsync(
                new PayoutNotice(
                    PayoutNoticeKind.Queued,
                    payout.Id,
                    SolAmount.FormatSol(payout.AmountLamports),
                    payout.DestinationFriendly,
                    payout.Comment,
                    null),
                budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Queued payout notice timed out.");
        }
    }

    internal async Task<PayoutDto?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var payout = await _store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return payout is null ? null : PayoutMapper.ToDto(payout, _options);
    }

    internal async Task<PayoutPageDto> ListAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > PayoutLimits.MaxListLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var (items, hasMore) = await _store.ListAsync(limit, offset, cancellationToken).ConfigureAwait(false);
        return new PayoutPageDto(limit, offset, hasMore, items.Select(item => PayoutMapper.ToDto(item, _options)).ToList());
    }

    internal async Task<PayoutSettingsSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var (paused, notify) = await _store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var (queued, review) = await _store.CountsAsync(cancellationToken).ConfigureAwait(false);
        return new PayoutSettingsSnapshot(
            paused,
            _gate.CanSign,
            _gate.LocalOk && !_gate.PermanentlyBlocked,
            _gate.Problem,
            SolAmount.FormatSol(_options.MaxPayoutLamports),
            SolAmount.FormatSol(_options.MinReserveLamports),
            queued,
            review,
            notify);
    }

    internal async Task<string> PauseAsync(CancellationToken cancellationToken)
    {
        await _store.SetPausedAsync(true, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Payouts paused.");
        return "Paused.";
    }

    internal async Task<string> ResumeAsync(CancellationToken cancellationToken)
    {
        if (!_gate.LocalOk || _gate.PermanentlyBlocked)
            return _gate.Problem ?? "Signing isn't available.";

        await _store.SetPausedAsync(false, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Payouts resumed.");
        return "Resumed.";
    }

    internal async Task<string> ToggleNotificationsAsync(CancellationToken cancellationToken)
    {
        var enabled = await _store.ToggleNotifyOnSuccessAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Payout success alerts {State}.", enabled ? "on" : "off");
        return enabled ? "Success alerts on." : "Success alerts off.";
    }

    private bool CanAfford(long balanceLamports, long reservedLamports, long need)
    {
        if (need <= 0 || balanceLamports < _options.MinReserveLamports)
            return false;

        var spendable = balanceLamports - _options.MinReserveLamports;
        return reservedLamports <= spendable && spendable - reservedLamports >= need;
    }

    private static int MapStatus(SolanaRpcErrorKind kind)
    {
        return kind switch
        {
            SolanaRpcErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
            SolanaRpcErrorKind.Timeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status502BadGateway
        };
    }
}
