using SolanaWalletService.Payouts;

namespace SolanaWalletService.Persistence;

public sealed class PayoutEntity
{
    public Guid Id { get; set; }

    public string RequestId { get; set; } = "";

    public string DestinationRaw { get; set; } = "";

    public string DestinationFriendly { get; set; } = "";

    public long AmountLamports { get; set; }

    public string? Comment { get; set; }

    public PayoutStatus Status { get; set; }

    public string? Signature { get; set; }

    public string? SignedTransaction { get; set; }

    public string? RecentBlockhash { get; set; }

    public long? LastValidBlockHeight { get; set; }

    public int Attempts { get; set; }

    public int ResignCount { get; set; }

    public int SignFailures { get; set; }

    public SendDisposition LastSend { get; set; }

    public DateTimeOffset? LastBroadcastAtUtc { get; set; }

    public long FeeReserveLamports { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? ConfirmedAtUtc { get; set; }

    public bool InsufficientNotified { get; set; }

    public bool TerminalNotified { get; set; }

    public bool SuccessNotified { get; set; }

    public List<PayoutTrackedSignatureEntity> Signatures { get; set; } = [];
}
