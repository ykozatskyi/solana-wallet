namespace SolanaWalletService.Persistence;

internal static class PostgresLocks
{
    public const long Wallet = 8242012026L;

    public const long Settings = 8242012027L;

    public static (int Hi, int Lo) Payout(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return (BitConverter.ToInt32(bytes), BitConverter.ToInt32(bytes[4..]));
    }
}
