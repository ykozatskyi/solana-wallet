namespace SolanaWalletService.Persistence;

public sealed class DepositCursorEntity
{
    public int Id { get; set; }

    public string? Signature { get; set; }

    public long Slot { get; set; }
}

public sealed class DepositNotificationEntity
{
    public string Signature { get; set; } = "";

    public long Slot { get; set; }

    public long AmountLamports { get; set; }

    public string? FromAddress { get; set; }

    public DateTimeOffset SeenAtUtc { get; set; }
}
