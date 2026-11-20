using SolanaWalletService.Configuration;
using SolanaWalletService.Solana;

namespace SolanaWalletService.Payouts;

internal sealed class PayoutProcessor
{
    private readonly PayoutStore _store;
    private readonly SolanaRpcClient _rpc;
    private readonly IWalletSigner _signer;
    private readonly WalletSigningGate _gate;
    private readonly IPayoutNotifier _notifier;
    private readonly AppOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PayoutProcessor> _logger;
    private readonly TimeSpan _outageAfter;
    private readonly SemaphoreSlim _run = new(1, 1);
    private DateTimeOffset? _outageSince;
    private bool _outageNotified;
    private bool _loggedBlock;

    public PayoutProcessor(
        PayoutStore store,
        SolanaRpcClient rpc,
        IWalletSigner signer,
        WalletSigningGate gate,
        IPayoutNotifier notifier,
        AppOptions options,
        TimeProvider time,
        ILogger<PayoutProcessor> logger,
        TimeSpan? outageAfter = null)
    {
        _store = store;
        _rpc = rpc;
        _signer = signer;
        _gate = gate;
        _notifier = notifier;
        _options = options;
        _time = time;
        _logger = logger;
        _outageAfter = outageAfter ?? PayoutLimits.OutageNotificationAfter;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshChainAsync(cancellationToken).ConfigureAwait(false);
            foreach (var payout in await _store.InFlightAsync(cancellationToken).ConfigureAwait(false))
                await ReconcileAsync(payout, cancellationToken).ConfigureAwait(false);

            if (await _store.HasNeedsReviewAsync(cancellationToken).ConfigureAwait(false)
                || await _store.HasInFlightAsync(cancellationToken).ConfigureAwait(false)
                || await _store.IsPausedAsync(cancellationToken).ConfigureAwait(false)
                || !_gate.CanSign)
            {
                return;
            }

            var next = await _store.NextQueuedAsync(cancellationToken).ConfigureAwait(false);
            if (next is not null)
                await SignAndBroadcastAsync(next, cancellationToken).ConfigureAwait(false);
        }
        catch (SolanaRpcException ex) when (IsTransport(ex))
        {
            _logger.LogWarning("Payout chain call failed ({Kind}).", ex.Kind);
            await NoteOutageAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _run.Release();
        }
    }

    private async Task RefreshChainAsync(CancellationToken cancellationToken)
    {
        if (_gate.PermanentlyBlocked || _gate.CanSign)
            return;

        var (account, balance) = await ReadWalletAsync(cancellationToken).ConfigureAwait(false);
        ApplyChain(WalletIdentity.Evaluate(_signer.Identity, account, balance));
    }

    private async Task SignAndBroadcastAsync(PayoutRecord payout, CancellationToken cancellationToken)
    {
        await using var lease = await _store.AcquireWalletLeaseAsync(cancellationToken).ConfigureAwait(false);
        var current = await _store.ReadAsync(lease, payout.Id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Status != PayoutStatus.Queued)
            return;

        if (await _store.HasInFlightAsync(lease, cancellationToken).ConfigureAwait(false)
            || await _store.HasNeedsReviewAsync(lease, cancellationToken).ConfigureAwait(false)
            || await _store.IsPausedAsync(lease, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await PreviousSignaturesAllowSigningAsync(lease, current, cancellationToken).ConfigureAwait(false))
            return;

        current = await _store.ReadAsync(lease, payout.Id, cancellationToken).ConfigureAwait(false) ?? current;
        if (current.Status != PayoutStatus.Queued)
            return;

        if (HasSignatureMaterial(current) || current.ResignCount > 0)
        {
            await FinishAsync(
                current.Id,
                PayoutStatus.NeedsReview,
                "Payout already has a signature and was not signed again.",
                current.Signature,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var (account, balance) = await ReadWalletAsync(cancellationToken).ConfigureAwait(false);
        var check = WalletIdentity.Evaluate(_signer.Identity, account, balance);
        ApplyChain(check);
        if (!check.Ok)
        {
            await _store.UpdateAsync(lease, current.Id, record => record.LastError = check.Problem ?? "Wallet state is incomplete.", cancellationToken).ConfigureAwait(false);
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var destination = await _rpc.GetAccountAsync(current.DestinationRaw, cancellationToken).ConfigureAwait(false);
        ChainReached();
        if (destination is null)
        {
            var rent = await _rpc.GetRentExemptMinimumAsync(cancellationToken).ConfigureAwait(false);
            ChainReached();
            if (current.AmountLamports < rent)
            {
                await FinishAsync(
                    current.Id,
                    PayoutStatus.Failed,
                    "Destination needs at least " + SolAmount.FormatSol(rent) + " SOL to be created.",
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var liveReserve = Math.Max(current.FeeReserveLamports, _options.FeeReserveLamports);
        var reserved = await _store.ReservedLamportsAsync(lease, current.Id, cancellationToken).ConfigureAwait(false);
        var need = current.AmountLamports + liveReserve;
        if (balance < _options.MinReserveLamports
            || reserved > balance - _options.MinReserveLamports
            || balance - _options.MinReserveLamports - reserved < need)
        {
            await NoteInsufficientAsync(current, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await _store.IsPausedAsync(lease, cancellationToken).ConfigureAwait(false))
            return;

        var blockhash = await _rpc.GetLatestBlockhashAsync(cancellationToken).ConfigureAwait(false);
        ChainReached();
        SignedTransfer signed;
        try
        {
            signed = _signer.Sign(new NativeTransfer(
                current.DestinationRaw,
                current.AmountLamports,
                current.Comment,
                blockhash.Blockhash,
                blockhash.LastValidBlockHeight,
                SolanaLimits.ComputeUnitLimit,
                SolanaLimits.PriorityPrice(_options.MaxPriorityFeeLamports)));
        }
        catch (InvalidOperationException)
        {
            await NoteSignFailureAsync(current.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var applied = false;
        await _store.UpdateAsync(lease, current.Id, record =>
        {
            if (record.Status != PayoutStatus.Queued || HasSignatureMaterial(record) || record.ResignCount > 0)
                return;

            applied = true;
            var from = record.Status;
            record.Status = PayoutStatus.Signed;
            record.Signature = signed.Signature;
            record.SignedTransaction = signed.TransactionBase64;
            record.RecentBlockhash = signed.RecentBlockhash;
            record.LastValidBlockHeight = signed.LastValidBlockHeight;
            record.FeeReserveLamports = liveReserve;
            Track(record, signed);
            record.LastBroadcastAtUtc = _time.GetUtcNow();
            record.LastSend = SendDisposition.None;
            record.Attempts++;
            record.LastError = null;
            record.InsufficientNotified = false;
            _logger.LogInformation(
                "Payout {PayoutId} {FromStatus} -> Signed ({Signature}).",
                record.Id,
                from,
                SolAddress.Shorten(signed.Signature));
        }, cancellationToken).ConfigureAwait(false);
        if (!applied)
            return;

        await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
        await BroadcastAsync(current.Id, signed.Signature, signed.TransactionBase64, skipPreflight: false, cancellationToken).ConfigureAwait(false);
        var saved = await _store.FindAsync(current.Id, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
            await ReconcileAsync(saved, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PreviousSignaturesAllowSigningAsync(WalletLease lease, PayoutRecord payout, CancellationToken cancellationToken)
    {
        if (payout.TrackedSignatures.Count == 0)
        {
            if (!HasSignatureMaterial(payout) && payout.ResignCount == 0)
                return true;

            await FinishAsync(
                payout.Id,
                PayoutStatus.NeedsReview,
                payout.ResignCount > 0
                    ? "A replacement was signed before the earlier transaction was proven unsuccessful."
                    : "Payout already has a signature and was not signed again.",
                payout.Signature,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (payout.TrackedSignatures.Count > 1)
        {
            await FinishAsync(
                payout.Id,
                PayoutStatus.NeedsReview,
                "A replacement was signed before the earlier transaction was proven unsuccessful.",
                payout.Signature,
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        var statuses = await ResolveStatusesAsync(payout.TrackedSignatures, null, cancellationToken).ConfigureAwait(false);
        if (statuses.Count != payout.TrackedSignatures.Count)
            return false;

        var found = -1;
        for (var i = 0; i < statuses.Count; i++)
        {
            if (!statuses[i].Found)
                continue;

            if (found >= 0)
            {
                await FinishAsync(payout.Id, PayoutStatus.NeedsReview, "More than one signature was found for this payout.", payout.Signature, cancellationToken).ConfigureAwait(false);
                return false;
            }

            found = i;
        }

        if (found >= 0)
        {
            var chosen = payout.TrackedSignatures[found];
            await _store.UpdateAsync(lease, payout.Id, record =>
            {
                if (record.Status != PayoutStatus.Queued)
                    return;

                if (!string.Equals(record.Signature, chosen.Signature, StringComparison.Ordinal))
                    record.SignedTransaction = null;

                record.Status = PayoutStatus.Broadcasted;
                record.Signature = chosen.Signature;
                record.RecentBlockhash = chosen.RecentBlockhash;
                record.LastValidBlockHeight = chosen.LastValidBlockHeight;
                record.LastError = null;
            }, cancellationToken).ConfigureAwait(false);
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var height = await _rpc.GetBlockHeightAsync("finalized", cancellationToken).ConfigureAwait(false);
        ChainReached();
        if (payout.TrackedSignatures.Any(item => !PastExpiryGrace(height, item.LastValidBlockHeight)))
        {
            await _store.UpdateAsync(lease, payout.Id, record => record.LastError = "Waiting until the previous signature expires.", cancellationToken).ConfigureAwait(false);
            await lease.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await FinishAsync(
            payout.Id,
            PayoutStatus.NeedsReview,
            "The blockhash expired and the payout was not found on-chain.",
            payout.Signature ?? payout.TrackedSignatures[0].Signature,
            cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task ReconcileAsync(PayoutRecord payout, CancellationToken cancellationToken)
    {
        var current = await _store.FindAsync(payout.Id, cancellationToken).ConfigureAwait(false) ?? payout;
        if (current.Status is not (PayoutStatus.Signed or PayoutStatus.Broadcasted))
            return;

        if (current.TrackedSignatures.Count == 0
            || current.LastValidBlockHeight is null
            || string.IsNullOrWhiteSpace(current.Signature))
        {
            await FinishAsync(current.Id, PayoutStatus.NeedsReview, "Signed payout is missing its signature.", current.Signature, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (current.TrackedSignatures.Count > 1)
        {
            await FinishAsync(
                current.Id,
                PayoutStatus.NeedsReview,
                "A replacement was signed before the earlier transaction was proven unsuccessful.",
                current.Signature,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var loaded = new Dictionary<string, SolanaChainTransaction>(StringComparer.Ordinal);
        var statuses = await ResolveStatusesAsync(current.TrackedSignatures, loaded, cancellationToken).ConfigureAwait(false);
        var height = await _rpc.GetBlockHeightAsync("finalized", cancellationToken).ConfigureAwait(false);
        ChainReached();

        string? foundSignature = null;
        var foundCount = 0;
        for (var i = 0; i < statuses.Count && i < current.TrackedSignatures.Count; i++)
        {
            if (!statuses[i].Found)
                continue;

            foundCount++;
            foundSignature = current.TrackedSignatures[i].Signature;
        }

        SolanaChainTransaction? transaction = null;
        if (foundCount == 1 && foundSignature is not null)
        {
            if (!loaded.TryGetValue(foundSignature, out var known))
            {
                transaction = await _rpc.GetTransactionAsync(foundSignature, _options.ConfirmationCommitment, cancellationToken).ConfigureAwait(false);
                ChainReached();
            }
            else
            {
                transaction = known;
            }
        }

        var observation = Observe(current, height);
        var verdict = PayoutVerifier.Review(observation, statuses, PayoutVerifier.Check(transaction, observation));
        switch (verdict.Kind)
        {
            case PayoutVerdictKind.Pending:
                return;
            case PayoutVerdictKind.Confirmed:
                await FinishAsync(current.Id, PayoutStatus.Confirmed, null, verdict.Signature ?? foundSignature, cancellationToken).ConfigureAwait(false);
                return;
            case PayoutVerdictKind.Failed:
                await FinishAsync(current.Id, PayoutStatus.Failed, verdict.Reason ?? "The transfer failed.", verdict.Signature ?? foundSignature, cancellationToken).ConfigureAwait(false);
                return;
            case PayoutVerdictKind.NeedsReview:
                await FinishAsync(current.Id, PayoutStatus.NeedsReview, verdict.Reason ?? "The transfer needs a look.", verdict.Signature ?? current.Signature, cancellationToken).ConfigureAwait(false);
                return;
            case PayoutVerdictKind.Rebroadcast:
                await RebroadcastAsync(current, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task RebroadcastAsync(PayoutRecord current, CancellationToken cancellationToken)
    {
        if (await _store.IsPausedAsync(cancellationToken).ConfigureAwait(false))
            return;

        if (string.IsNullOrWhiteSpace(current.SignedTransaction) || string.IsNullOrWhiteSpace(current.Signature))
        {
            await FinishAsync(current.Id, PayoutStatus.NeedsReview, "Signed payout is missing its transaction.", current.Signature, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (current.LastBroadcastAtUtc is { } sentAt
            && _time.GetUtcNow() - sentAt < TimeSpan.FromSeconds(PayoutLimits.RebroadcastDelaySeconds))
        {
            return;
        }

        await _store.UpdateAsync(current.Id, record =>
        {
            record.LastBroadcastAtUtc = _time.GetUtcNow();
            record.Attempts++;
        }, cancellationToken).ConfigureAwait(false);
        await BroadcastAsync(current.Id, current.Signature, current.SignedTransaction, skipPreflight: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastAsync(Guid id, string expectedSignature, string transactionBase64, bool skipPreflight, CancellationToken cancellationToken)
    {
        try
        {
            var signature = await _rpc.SendTransactionAsync(transactionBase64, skipPreflight, cancellationToken).ConfigureAwait(false);
            ChainReached();
            if (!string.Equals(signature, expectedSignature, StringComparison.Ordinal))
            {
                await FinishAsync(id, PayoutStatus.NeedsReview, "Solana RPC returned a different signature.", expectedSignature, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _store.UpdateAsync(id, record =>
            {
                if (record.Status is PayoutStatus.Confirmed or PayoutStatus.Failed or PayoutStatus.NeedsReview)
                    return;

                var from = record.Status;
                record.Status = PayoutStatus.Broadcasted;
                record.LastSend = SendDisposition.Accepted;
                record.Signature = expectedSignature;
                record.LastError = null;
                _logger.LogInformation("Payout {PayoutId} {FromStatus} -> Broadcasted.", id, from);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SolanaRpcException ex) when (ex.Kind == SolanaRpcErrorKind.Duplicate)
        {
            ChainReached();
            await _store.UpdateAsync(id, record =>
            {
                if (record.Status is PayoutStatus.Confirmed or PayoutStatus.Failed or PayoutStatus.NeedsReview)
                    return;

                record.Status = PayoutStatus.Broadcasted;
                record.LastSend = SendDisposition.Accepted;
                record.LastError = null;
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Payout {PayoutId} transaction is already known.", id);
        }
        catch (SolanaRpcException ex) when (ex.Kind == SolanaRpcErrorKind.Rejected)
        {
            ChainReached();
            await _store.UpdateAsync(id, record =>
            {
                if (record.Status is PayoutStatus.Confirmed or PayoutStatus.Failed or PayoutStatus.NeedsReview)
                    return;

                SendDispositionRules.NoteRejected(record, ex.Message);
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Payout {PayoutId} was rejected by Solana RPC.", id);
        }
        catch (SolanaRpcException ex) when (IsTransport(ex))
        {
            await _store.UpdateAsync(id, record =>
            {
                if (record.Status is PayoutStatus.Confirmed or PayoutStatus.Failed or PayoutStatus.NeedsReview)
                    return;

                SendDispositionRules.NoteUncertain(record, ex.Message);
            }, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Payout {PayoutId} broadcast is uncertain ({Kind}).", id, ex.Kind);
            await NoteOutageAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NoteInsufficientAsync(PayoutRecord payout, CancellationToken cancellationToken)
    {
        var notify = false;
        await _store.UpdateAsync(payout.Id, record =>
        {
            record.LastError = "Not enough SOL is available.";
            if (record.InsufficientNotified)
                return;

            record.InsufficientNotified = true;
            notify = true;
        }, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Payout {PayoutId} is waiting for balance.", payout.Id);
        if (!notify)
            return;

        await NotifyAsync(payout, PayoutNoticeKind.InsufficientBalance, "Not enough SOL is available.", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task NoteSignFailureAsync(Guid id, CancellationToken cancellationToken)
    {
        var review = false;
        await _store.UpdateAsync(id, record =>
        {
            record.SignFailures++;
            record.LastError = "Couldn't sign the transfer.";
            review = record.SignFailures >= 3
                && record.Status is not (PayoutStatus.Confirmed or PayoutStatus.Failed or PayoutStatus.NeedsReview);
        }, cancellationToken).ConfigureAwait(false);
        if (review)
            await FinishAsync(id, PayoutStatus.NeedsReview, "Couldn't sign the transfer.", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(Guid id, PayoutStatus status, string? reason, string? signature, CancellationToken cancellationToken)
    {
        PayoutRecord? snapshot = null;
        var notify = false;
        var notifyOnSuccess = await _store.NotifyOnSuccessAsync(cancellationToken).ConfigureAwait(false);
        await _store.UpdateAsync(id, record =>
        {
            if (record.Status is PayoutStatus.Confirmed or PayoutStatus.Failed or PayoutStatus.NeedsReview)
            {
                snapshot = record.Clone();
                return;
            }

            var from = record.Status;
            record.Status = status;
            if (!string.IsNullOrWhiteSpace(signature))
                record.Signature = signature;

            record.LastError = reason;
            if (status == PayoutStatus.Confirmed)
            {
                record.ConfirmedAtUtc = _time.GetUtcNow();
                record.LastError = null;
                notify = notifyOnSuccess && !record.SuccessNotified;
                record.SuccessNotified = true;
            }
            else
            {
                notify = !record.TerminalNotified;
                record.TerminalNotified = true;
            }

            snapshot = record.Clone();
            _logger.LogInformation("Payout {PayoutId} {FromStatus} -> {ToStatus}.", id, from, status);
        }, cancellationToken).ConfigureAwait(false);

        if (!notify || snapshot is null)
            return;

        var kind = status switch
        {
            PayoutStatus.Confirmed => PayoutNoticeKind.Confirmed,
            PayoutStatus.Failed => PayoutNoticeKind.Failed,
            _ => PayoutNoticeKind.NeedsReview
        };
        await NotifyAsync(snapshot, kind, reason, snapshot.Signature, cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyAsync(PayoutRecord payout, PayoutNoticeKind kind, string? detail, string? signature, CancellationToken cancellationToken)
    {
        var explorer = string.IsNullOrWhiteSpace(signature) ? null : _options.TransactionExplorerUrl(signature);
        await _notifier.NotifyAsync(
            new PayoutNotice(
                kind,
                payout.Id,
                SolAmount.FormatSol(payout.AmountLamports),
                payout.DestinationFriendly,
                detail,
                explorer),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(SolanaAccount? Account, long Balance)> ReadWalletAsync(CancellationToken cancellationToken)
    {
        var account = await _rpc.GetAccountAsync(_options.WalletAddress, cancellationToken).ConfigureAwait(false);
        ChainReached();
        return (account, account?.Lamports ?? 0);
    }

    private PayoutObservation Observe(PayoutRecord payout, long finalizedHeight)
    {
        return new PayoutObservation(
            _options.WalletAddress,
            payout.DestinationRaw,
            payout.AmountLamports,
            payout.Comment,
            payout.Signature,
            payout.LastValidBlockHeight ?? 0,
            finalizedHeight,
            payout.LastSend,
            payout.LastError,
            _options.ConfirmationCommitment,
            payout.TrackedSignatures);
    }

    private void ApplyChain(ChainCheck check)
    {
        _gate.ApplyChain(check);
        if (!check.Permanent || _loggedBlock)
            return;

        _loggedBlock = true;
        _logger.LogError("Payout signing blocked: {Reason}", check.Problem);
    }

    private void ChainReached()
    {
        _outageSince = null;
        _outageNotified = false;
    }

    private async Task NoteOutageAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        _outageSince ??= now;
        if (_outageNotified || now - _outageSince < _outageAfter)
            return;

        _outageNotified = true;
        await _notifier.NotifyAsync(
            new PayoutNotice(PayoutNoticeKind.RpcDown, null, null, null, "Solana RPC has been unreachable.", null),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SolanaSignatureStatus>> ResolveStatusesAsync(
        IReadOnlyList<TrackedSignature> signatures,
        Dictionary<string, SolanaChainTransaction>? loaded,
        CancellationToken cancellationToken)
    {
        var reported = await _rpc.GetSignatureStatusesAsync(
            signatures.Select(item => item.Signature).ToList(),
            cancellationToken).ConfigureAwait(false);
        ChainReached();
        if (reported.Count != signatures.Count)
            return reported;

        var statuses = reported.ToList();
        for (var i = 0; i < statuses.Count; i++)
        {
            if (statuses[i].Found)
                continue;

            var transaction = await _rpc.GetTransactionAsync(
                signatures[i].Signature,
                _options.ConfirmationCommitment,
                cancellationToken).ConfigureAwait(false);
            ChainReached();
            if (transaction is null)
                continue;

            statuses[i] = new SolanaSignatureStatus(
                true,
                _options.ConfirmationCommitment,
                transaction.Failed,
                transaction.Error,
                false);
            if (loaded is not null)
                loaded[signatures[i].Signature] = transaction;
        }

        return statuses;
    }

    private static bool PastExpiryGrace(long finalizedHeight, long lastValidBlockHeight)
    {
        return finalizedHeight > lastValidBlockHeight + PayoutLimits.ExpiryGraceSlots;
    }

    private static bool HasSignatureMaterial(PayoutRecord payout)
    {
        return payout.TrackedSignatures.Count > 0
            || !string.IsNullOrWhiteSpace(payout.Signature)
            || !string.IsNullOrWhiteSpace(payout.SignedTransaction);
    }

    private static void Track(PayoutRecord payout, SignedTransfer signed)
    {
        if (payout.TrackedSignatures.Any(existing => string.Equals(existing.Signature, signed.Signature, StringComparison.Ordinal)))
            return;

        payout.TrackedSignatures.Add(new TrackedSignature(signed.Signature, signed.RecentBlockhash, signed.LastValidBlockHeight));
    }

    private static bool IsTransport(SolanaRpcException exception)
    {
        return exception.Kind is SolanaRpcErrorKind.Unavailable or SolanaRpcErrorKind.Timeout or SolanaRpcErrorKind.RateLimited;
    }
}
