using SolanaWalletService.Solana;

namespace SolanaWalletService.Payouts;

internal enum PayoutVerdictKind
{
    Pending,
    Confirmed,
    Failed,
    NeedsReview,
    Rebroadcast
}

internal readonly record struct PayoutVerdict(PayoutVerdictKind Kind, string? Signature, string? Reason)
{
    public static PayoutVerdict Pending() => new(PayoutVerdictKind.Pending, null, null);

    public static PayoutVerdict Confirmed(string signature) => new(PayoutVerdictKind.Confirmed, signature, null);

    public static PayoutVerdict Failed(string? signature, string reason) => new(PayoutVerdictKind.Failed, signature, reason);

    public static PayoutVerdict NeedsReview(string? signature, string reason) => new(PayoutVerdictKind.NeedsReview, signature, reason);

    public static PayoutVerdict Rebroadcast() => new(PayoutVerdictKind.Rebroadcast, null, null);
}

internal readonly record struct TransferCheck(
    bool Loaded,
    bool Failed,
    long DestinationDelta,
    long SourceDelta,
    long FeeLamports,
    bool MemoMatches,
    bool UnexpectedCredit);

internal readonly record struct PayoutObservation(
    string Wallet,
    string Destination,
    long AmountLamports,
    string? Comment,
    string? ActiveSignature,
    long LastValidBlockHeight,
    long? FinalizedBlockHeight,
    SendDisposition LastSend,
    string? LastError,
    string RequiredCommitment,
    IReadOnlyList<TrackedSignature> Signatures);

internal static class PayoutVerifier
{
    public static PayoutVerdict Review(
        PayoutObservation observation,
        IReadOnlyList<SolanaSignatureStatus> statuses,
        TransferCheck transfer)
    {
        if (statuses.Count != observation.Signatures.Count)
            return PayoutVerdict.Pending();

        var found = new List<int>();
        for (var i = 0; i < statuses.Count; i++)
        {
            if (statuses[i].Found)
                found.Add(i);
        }

        if (observation.Signatures.Count > 1 || found.Count > 1)
        {
            return PayoutVerdict.NeedsReview(
                observation.ActiveSignature,
                found.Count > 1
                    ? "More than one signature was found for this payout."
                    : "A replacement was signed before the earlier transaction was proven unsuccessful.");
        }

        if (found.Count == 1)
            return Found(observation, statuses[found[0]], observation.Signatures[found[0]].Signature, transfer);

        return Missing(observation);
    }

    public static void SelfCheck()
    {
        SendDispositionRules.SelfCheck();
        var signature = new TrackedSignature("sig", "blockhash", 1_000);
        var expired = 1_000 + PayoutLimits.ExpiryGraceSlots + 1;
        var baseObservation = new PayoutObservation(
            "wallet",
            "dest",
            25_000_000,
            "memo",
            "sig",
            1_000,
            1_000,
            SendDisposition.Uncertain,
            null,
            "finalized",
            [signature]);
        var missing = new SolanaSignatureStatus(false, null, false, null, false);
        Expect(Review(baseObservation, [missing], default), PayoutVerdictKind.Rebroadcast, "live");
        Expect(Review(baseObservation with { FinalizedBlockHeight = 1_001 }, [missing], default), PayoutVerdictKind.Pending, "grace");
        Expect(
            Review(baseObservation with { FinalizedBlockHeight = expired }, [missing], default),
            PayoutVerdictKind.NeedsReview,
            "uncertain");
        Expect(
            Review(
                baseObservation with
                {
                    FinalizedBlockHeight = expired,
                    LastSend = SendDisposition.Uncertain,
                    LastError = "Blockhash not found"
                },
                [missing],
                default),
            PayoutVerdictKind.NeedsReview,
            "timeout then blockhash");
        Expect(
            Review(
                baseObservation with
                {
                    FinalizedBlockHeight = expired,
                    LastSend = SendDisposition.Accepted
                },
                [missing],
                default),
            PayoutVerdictKind.NeedsReview,
            "accepted missing");
        Expect(
            Review(
                baseObservation with
                {
                    FinalizedBlockHeight = expired,
                    LastSend = SendDisposition.None
                },
                [missing],
                default),
            PayoutVerdictKind.NeedsReview,
            "restart before broadcast result");
        Expect(
            Review(
                baseObservation with
                {
                    FinalizedBlockHeight = expired,
                    LastSend = SendDisposition.Rejected,
                    LastError = "Blockhash not found"
                },
                [missing],
                default),
            PayoutVerdictKind.NeedsReview,
            "rejected blockhash");
        Expect(
            Review(
                baseObservation with
                {
                    LastSend = SendDisposition.Rejected,
                    LastError = "Blockhash not found"
                },
                [missing],
                default),
            PayoutVerdictKind.Pending,
            "rejected while blockhash is live");
        Expect(
            Review(
                baseObservation with
                {
                    FinalizedBlockHeight = expired,
                    LastSend = SendDisposition.Rejected,
                    LastError = "insufficient lamports"
                },
                [missing],
                default),
            PayoutVerdictKind.NeedsReview,
            "insufficient");
        Expect(
            Review(
                baseObservation with
                {
                    FinalizedBlockHeight = expired,
                    LastSend = SendDisposition.Rejected,
                    LastError = "insufficient funds for rent"
                },
                [missing],
                default),
            PayoutVerdictKind.NeedsReview,
            "rent");
        Expect(
            Review(
                baseObservation with
                {
                    Signatures =
                    [
                        signature,
                        new TrackedSignature("sig2", "blockhash2", 2_000)
                    ]
                },
                [missing, missing],
                default),
            PayoutVerdictKind.NeedsReview,
            "replacement");
        var finalized = new SolanaSignatureStatus(true, "finalized", false, null, false);
        Expect(
            Review(
                baseObservation,
                [finalized],
                new TransferCheck(true, false, 25_000_000, -(25_000_000 + 5_000), 5_000, true, false)),
            PayoutVerdictKind.Confirmed,
            "confirmed");
        Expect(
            Review(
                baseObservation,
                [finalized],
                new TransferCheck(true, false, 1, -1, 5_000, true, false)),
            PayoutVerdictKind.NeedsReview,
            "mismatch");
        Expect(
            Review(
                baseObservation,
                [new SolanaSignatureStatus(true, "finalized", true, "err", false)],
                new TransferCheck(true, true, 0, -5_000, 5_000, true, false)),
            PayoutVerdictKind.Failed,
            "failed on chain");
        Expect(
            Review(
                baseObservation,
                [new SolanaSignatureStatus(true, "processed", false, null, false)],
                new TransferCheck(true, false, 25_000_000, -(25_000_000 + 5_000), 5_000, true, false)),
            PayoutVerdictKind.Pending,
            "processed");
    }

    private static void Expect(PayoutVerdict verdict, PayoutVerdictKind kind, string name)
    {
        if (verdict.Kind != kind)
            throw new InvalidOperationException($"Payout recovery self-check failed ({name}).");
    }

    public static TransferCheck Check(SolanaChainTransaction? transaction, PayoutObservation observation)
    {
        if (transaction is null)
            return default;

        var destinationDelta = Delta(transaction, observation.Destination);
        var sourceDelta = Delta(transaction, observation.Wallet);
        var unexpected = transaction.BalanceDeltas.Any(pair =>
            pair.Value != 0
            && !string.Equals(pair.Key, observation.Wallet, StringComparison.Ordinal)
            && !string.Equals(pair.Key, observation.Destination, StringComparison.Ordinal));
        var memoMatches = MemoOk(observation.Comment, transaction.Memos);
        return new TransferCheck(
            true,
            transaction.Failed,
            destinationDelta,
            sourceDelta,
            transaction.FeeLamports,
            memoMatches,
            unexpected);
    }

    private static PayoutVerdict Found(
        PayoutObservation observation,
        SolanaSignatureStatus status,
        string signature,
        TransferCheck transfer)
    {
        if (Rank(status) < Rank(observation.RequiredCommitment))
            return PayoutVerdict.Pending();

        if (!transfer.Loaded)
            return PayoutVerdict.Pending();

        if (status.HasError || transfer.Failed)
        {
            return transfer.DestinationDelta == 0
                ? PayoutVerdict.Failed(signature, "The transfer failed on-chain.")
                : PayoutVerdict.NeedsReview(signature, "The transaction failed after moving SOL.");
        }

        if (!transfer.MemoMatches)
            return PayoutVerdict.NeedsReview(signature, "Transfer memo does not match.");

        if (transfer.UnexpectedCredit)
            return PayoutVerdict.NeedsReview(signature, "The transaction moved SOL to an unexpected account.");

        if (transfer.DestinationDelta != observation.AmountLamports)
            return PayoutVerdict.NeedsReview(signature, "Destination amount does not match.");

        var expectedSource = -(observation.AmountLamports + transfer.FeeLamports);
        if (transfer.SourceDelta != expectedSource)
            return PayoutVerdict.NeedsReview(signature, "Source debit does not match the payout and fee.");

        return PayoutVerdict.Confirmed(signature);
    }

    private static PayoutVerdict Missing(PayoutObservation observation)
    {
        if (observation.FinalizedBlockHeight is not long height)
            return PayoutVerdict.Pending();

        var expired = height > observation.LastValidBlockHeight;
        var gracePassed = height > observation.LastValidBlockHeight + PayoutLimits.ExpiryGraceSlots;
        if (!expired)
        {
            return observation.LastSend == SendDisposition.Rejected
                ? PayoutVerdict.Pending()
                : PayoutVerdict.Rebroadcast();
        }

        if (!gracePassed)
            return PayoutVerdict.Pending();

        // The signature is absent. That does not prove the transfer failed, so keep the payout in review.
        return PayoutVerdict.NeedsReview(observation.ActiveSignature, MissingReason(observation));
    }

    private static string MissingReason(PayoutObservation observation)
    {
        if (observation.LastSend == SendDisposition.Uncertain)
            return "Broadcast was uncertain and the payout was not found on-chain.";

        if (observation.LastSend == SendDisposition.Rejected && IsRent(observation.LastError))
            return "Preflight reported a rent error and the signature was not found on-chain.";

        if (observation.LastSend == SendDisposition.Rejected && IsInsufficient(observation.LastError))
            return "Preflight reported insufficient SOL and the signature was not found on-chain.";

        if (observation.LastSend == SendDisposition.Rejected && IsBlockhash(observation.LastError))
            return "The blockhash expired and the signature was not found on-chain.";

        if (observation.LastSend == SendDisposition.Rejected)
            return "The transaction was rejected and the signature was not found on-chain.";

        return "The blockhash expired and the payout was not found on-chain.";
    }

    private static bool MemoOk(string? comment, IReadOnlyList<string> memos)
    {
        if (comment is null)
            return memos.Count == 0;

        return memos.Count > 0 && memos.All(memo => string.Equals(memo, comment, StringComparison.Ordinal));
    }

    private static long Delta(SolanaChainTransaction transaction, string address)
    {
        return transaction.BalanceDeltas.TryGetValue(address, out var delta) ? delta : 0;
    }

    private static int Rank(SolanaSignatureStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.ConfirmationStatus))
            return Rank(status.ConfirmationStatus);

        return status.LegacyRooted && !status.HasError ? Rank("finalized") : 0;
    }

    private static int Rank(string? commitment)
    {
        return commitment?.ToLowerInvariant() switch
        {
            "finalized" => 3,
            "confirmed" => 2,
            "processed" => 1,
            _ => 0
        };
    }

    private static bool IsInsufficient(string? error)
    {
        return error is not null
            && (error.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
                || error.Contains("no record of a prior credit", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBlockhash(string? error)
    {
        return error is not null && error.Contains("blockhash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRent(string? error)
    {
        return error is not null && error.Contains("rent", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class SendDispositionRules
{
    public static void NoteRejected(PayoutRecord record, string message)
    {
        if (record.LastSend != SendDisposition.None)
            return;

        record.LastSend = SendDisposition.Rejected;
        record.LastError = message;
    }

    public static void NoteUncertain(PayoutRecord record, string message)
    {
        if (record.LastSend is SendDisposition.Accepted or SendDisposition.Uncertain)
            return;

        record.LastSend = SendDisposition.Uncertain;
        record.LastError = message;
    }

    public static void SelfCheck()
    {
        var accepted = new PayoutRecord { LastSend = SendDisposition.Accepted, LastError = "accepted" };
        NoteRejected(accepted, "Blockhash not found");
        NoteUncertain(accepted, "Solana RPC timed out.");
        var uncertain = new PayoutRecord { LastSend = SendDisposition.Uncertain, LastError = "Solana RPC timed out." };
        NoteRejected(uncertain, "Blockhash not found");
        var rejected = new PayoutRecord { LastSend = SendDisposition.Rejected, LastError = "Blockhash not found" };
        NoteUncertain(rejected, "Solana RPC timed out.");
        var fresh = new PayoutRecord();
        NoteRejected(fresh, "Blockhash not found");

        if (accepted.LastSend != SendDisposition.Accepted
            || accepted.LastError != "accepted"
            || uncertain.LastSend != SendDisposition.Uncertain
            || uncertain.LastError != "Solana RPC timed out."
            || rejected.LastSend != SendDisposition.Uncertain
            || rejected.LastError != "Solana RPC timed out."
            || fresh.LastSend != SendDisposition.Rejected
            || fresh.LastError != "Blockhash not found")
        {
            throw new InvalidOperationException("Payout send disposition self-check failed.");
        }
    }
}
