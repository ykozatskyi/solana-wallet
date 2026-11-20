namespace SolanaWalletService.Persistence;

public sealed class PayoutSettingsEntity
{
    public int Id { get; set; }

    public bool Paused { get; set; }

    public bool NotifyOnSuccess { get; set; }
}
