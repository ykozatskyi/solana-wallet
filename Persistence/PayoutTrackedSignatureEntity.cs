namespace SolanaWalletService.Persistence;

public sealed class PayoutTrackedSignatureEntity
{
    public Guid PayoutId { get; set; }

    public int Ordinal { get; set; }

    public string Signature { get; set; } = "";

    public string RecentBlockhash { get; set; } = "";

    public long LastValidBlockHeight { get; set; }

    public PayoutEntity Payout { get; set; } = null!;
}
