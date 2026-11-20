namespace SolanaWalletService.Payouts;

internal enum PayoutNoticeKind
{
    InsufficientBalance,
    Failed,
    NeedsReview,
    RpcDown,
    Confirmed,
    Queued
}

internal sealed record PayoutNotice(
    PayoutNoticeKind Kind,
    Guid? PayoutId,
    string? Amount,
    string? Destination,
    string? Detail,
    string? ExplorerUrl);

internal sealed record DepositCredit(string Amount, string? From);

internal sealed record DepositNotice(
    IReadOnlyList<DepositCredit> Credits,
    string? Balance,
    string? ExplorerUrl);

internal interface IPayoutNotifier
{
    Task NotifyAsync(PayoutNotice notice, CancellationToken cancellationToken);

    Task NotifyDepositAsync(DepositNotice notice, CancellationToken cancellationToken);
}
