using System.Text.Json.Serialization;

namespace SolanaWalletService.Models;

public sealed class CreatePayoutRequest
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("amount")]
    public System.Text.Json.JsonElement Amount { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public sealed record PayoutAcceptedDto(Guid Id, string Status);

public sealed record PayoutDto(
    Guid Id,
    string RequestId,
    string Address,
    string Amount,
    string? Comment,
    string Status,
    string? TransactionHash,
    string? ExplorerUrl,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc);

public sealed record PayoutPageDto(
    int Limit,
    int Offset,
    bool HasMore,
    IReadOnlyList<PayoutDto> Payouts);
