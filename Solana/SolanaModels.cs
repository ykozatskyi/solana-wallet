namespace SolanaWalletService.Solana;

internal static class SolanaLimits
{
    public const long SignatureFeeLamports = 5_000;

    public const uint ComputeUnitLimit = 50_000;

    public static ulong PriorityPrice(long maxPriorityFeeLamports)
    {
        if (maxPriorityFeeLamports <= 0)
            return 0;

        return (ulong)maxPriorityFeeLamports * 1_000_000UL / ComputeUnitLimit;
    }
}

internal enum SolanaRpcErrorKind
{
    Unavailable,
    Timeout,
    RateLimited,
    InvalidResponse,
    Rejected,
    Duplicate
}

internal sealed class SolanaRpcException : Exception
{
    public SolanaRpcException(SolanaRpcErrorKind kind, string message, int? statusCode = null)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public SolanaRpcErrorKind Kind { get; }

    public int? StatusCode { get; }
}

internal sealed record SolanaAccount(long Lamports, string Owner, bool Executable, int Space);

internal sealed record SolanaBlockhash(string Blockhash, long LastValidBlockHeight);

internal sealed record SolanaSignatureInfo(
    string Signature,
    long Slot,
    DateTimeOffset? BlockTime,
    bool Failed,
    string? ConfirmationStatus);

internal sealed record SolanaSignatureStatus(
    bool Found,
    string? ConfirmationStatus,
    bool HasError,
    string? Error,
    bool LegacyRooted);

internal sealed class SolanaChainTransaction
{
    public required string Signature { get; init; }

    public required long Slot { get; init; }

    public DateTimeOffset? BlockTime { get; init; }

    public required bool Failed { get; init; }

    public string? Error { get; init; }

    public required long FeeLamports { get; init; }

    public required IReadOnlyList<Models.WalletMessageDto> Messages { get; init; }

    public required IReadOnlyDictionary<string, long> BalanceDeltas { get; init; }

    public required IReadOnlyList<string> Memos { get; init; }
}
