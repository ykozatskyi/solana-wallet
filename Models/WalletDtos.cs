namespace SolanaWalletService.Models;

public sealed record ErrorResponse(string Error);

public sealed record HealthResponse(string Status);

public sealed record WalletAddressDto(
    string Address,
    string BounceableAddress,
    string RawAddress,
    string Network,
    string ExplorerUrl);

public sealed record WalletBalanceDto(
    string Address,
    string Network,
    string Balance,
    string BalanceNano,
    string Status,
    bool Initialized,
    DateTimeOffset RetrievedAtUtc);

public sealed record TransactionPageDto(
    int Limit,
    int Offset,
    bool HasMore,
    IReadOnlyList<WalletTransactionDto> Transactions);

public sealed record WalletTransactionDto(
    string Hash,
    string LogicalTime,
    DateTimeOffset? TimestampUtc,
    string? Fee,
    string? FeeNano,
    bool? Aborted,
    string? OrigStatus,
    string? EndStatus,
    string ExplorerUrl,
    IReadOnlyList<WalletMessageDto> Messages);

public sealed record WalletMessageDto(
    string Direction,
    string? Amount,
    string? AmountNano,
    string? Counterparty,
    string? Opcode,
    bool Bounced);

public static class MessageDirections
{
    public const string Incoming = "incoming";
    public const string Outgoing = "outgoing";
    public const string Other = "other";
}
