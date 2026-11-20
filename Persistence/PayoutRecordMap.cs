using SolanaWalletService.Payouts;

namespace SolanaWalletService.Persistence;

internal static class PayoutRecordMap
{
    public static PayoutEntity ToEntity(PayoutRecord record)
    {
        var entity = new PayoutEntity
        {
            Id = record.Id,
            Signatures = []
        };
        Copy(entity, record, replaceSignatures: true);
        return entity;
    }

    public static PayoutRecord ToRecord(PayoutEntity entity)
    {
        var record = new PayoutRecord
        {
            Id = entity.Id,
            RequestId = entity.RequestId,
            DestinationRaw = entity.DestinationRaw,
            DestinationFriendly = entity.DestinationFriendly,
            AmountLamports = entity.AmountLamports,
            Comment = entity.Comment,
            Status = entity.Status,
            Signature = entity.Signature,
            SignedTransaction = entity.SignedTransaction,
            RecentBlockhash = entity.RecentBlockhash,
            LastValidBlockHeight = entity.LastValidBlockHeight,
            Attempts = entity.Attempts,
            ResignCount = entity.ResignCount,
            SignFailures = entity.SignFailures,
            LastSend = entity.LastSend,
            LastBroadcastAtUtc = entity.LastBroadcastAtUtc,
            FeeReserveLamports = entity.FeeReserveLamports,
            LastError = entity.LastError,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            ConfirmedAtUtc = entity.ConfirmedAtUtc,
            InsufficientNotified = entity.InsufficientNotified,
            TerminalNotified = entity.TerminalNotified,
            SuccessNotified = entity.SuccessNotified
        };

        if (entity.Signatures is { Count: > 0 })
        {
            foreach (var signature in entity.Signatures.OrderBy(item => item.Ordinal))
            {
                record.TrackedSignatures.Add(new TrackedSignature(
                    signature.Signature,
                    signature.RecentBlockhash,
                    signature.LastValidBlockHeight));
            }
        }

        return record;
    }

    public static void Copy(PayoutEntity entity, PayoutRecord record, bool replaceSignatures)
    {
        entity.RequestId = record.RequestId;
        entity.DestinationRaw = record.DestinationRaw;
        entity.DestinationFriendly = record.DestinationFriendly;
        entity.AmountLamports = record.AmountLamports;
        entity.Comment = record.Comment;
        entity.Status = record.Status;
        entity.Signature = record.Signature;
        entity.SignedTransaction = record.SignedTransaction;
        entity.RecentBlockhash = record.RecentBlockhash;
        entity.LastValidBlockHeight = record.LastValidBlockHeight;
        entity.Attempts = record.Attempts;
        entity.ResignCount = record.ResignCount;
        entity.SignFailures = record.SignFailures;
        entity.LastSend = record.LastSend;
        entity.LastBroadcastAtUtc = record.LastBroadcastAtUtc;
        entity.FeeReserveLamports = record.FeeReserveLamports;
        entity.LastError = record.LastError;
        entity.CreatedAtUtc = record.CreatedAtUtc;
        entity.UpdatedAtUtc = record.UpdatedAtUtc;
        entity.ConfirmedAtUtc = record.ConfirmedAtUtc;
        entity.InsufficientNotified = record.InsufficientNotified;
        entity.TerminalNotified = record.TerminalNotified;
        entity.SuccessNotified = record.SuccessNotified;
        SyncSignatures(entity, record, replaceSignatures);
    }

    private static void SyncSignatures(PayoutEntity entity, PayoutRecord record, bool replaceSignatures)
    {
        var ordered = entity.Signatures.OrderBy(signature => signature.Ordinal).ToList();
        if (ordered.Count == record.TrackedSignatures.Count
            && !replaceSignatures
            && PrefixEquals(ordered, record, ordered.Count))
        {
            return;
        }

        if (!replaceSignatures
            && ordered.Count < record.TrackedSignatures.Count
            && PrefixEquals(ordered, record, ordered.Count))
        {
            for (var i = ordered.Count; i < record.TrackedSignatures.Count; i++)
            {
                var tracked = record.TrackedSignatures[i];
                entity.Signatures.Add(new PayoutTrackedSignatureEntity
                {
                    PayoutId = entity.Id,
                    Ordinal = i,
                    Signature = tracked.Signature,
                    RecentBlockhash = tracked.RecentBlockhash,
                    LastValidBlockHeight = tracked.LastValidBlockHeight
                });
            }

            return;
        }

        entity.Signatures.Clear();
        for (var i = 0; i < record.TrackedSignatures.Count; i++)
        {
            var tracked = record.TrackedSignatures[i];
            entity.Signatures.Add(new PayoutTrackedSignatureEntity
            {
                PayoutId = entity.Id,
                Ordinal = i,
                Signature = tracked.Signature,
                RecentBlockhash = tracked.RecentBlockhash,
                LastValidBlockHeight = tracked.LastValidBlockHeight
            });
        }
    }

    private static bool PrefixEquals(List<PayoutTrackedSignatureEntity> ordered, PayoutRecord record, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(ordered[i].Signature, record.TrackedSignatures[i].Signature, StringComparison.Ordinal)
                || !string.Equals(ordered[i].RecentBlockhash, record.TrackedSignatures[i].RecentBlockhash, StringComparison.Ordinal)
                || ordered[i].LastValidBlockHeight != record.TrackedSignatures[i].LastValidBlockHeight)
            {
                return false;
            }
        }

        return true;
    }
}
