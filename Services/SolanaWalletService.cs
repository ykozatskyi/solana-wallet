using System.Globalization;
using SolanaWalletService.Configuration;
using SolanaWalletService.Models;
using SolanaWalletService.Payouts;
using SolanaWalletService.Solana;

namespace SolanaWalletService.Services;

internal sealed class SolanaWalletService
{
    public const int MaxTransactionLimit = 50;
    public const int TelegramPageSize = 10;

    private readonly SolanaRpcClient _rpc;
    private readonly AppOptions _options;
    private readonly WalletAddressDto _address;
    private readonly ILogger<SolanaWalletService> _logger;

    public SolanaWalletService(SolanaRpcClient rpc, AppOptions options, ILogger<SolanaWalletService> logger)
    {
        _rpc = rpc;
        _options = options;
        _logger = logger;
        _address = new WalletAddressDto(
            options.WalletAddress,
            options.WalletAddress,
            options.WalletAddress,
            options.Network,
            options.AddressExplorerUrl);
    }

    public static void SelfCheck()
    {
        SolAmount.SelfCheck();
        SolAddress.SelfCheck();
        SolanaTransactionReader.SelfCheck();
        var sample = new SolanaChainTransaction
        {
            Signature = "sig",
            Slot = 9,
            BlockTime = DateTimeOffset.FromUnixTimeSeconds(1700000000),
            Failed = false,
            FeeLamports = 5_000,
            Messages =
            [
                new WalletMessageDto(MessageDirections.Incoming, "0.025", "25000000", "from", "transfer", false)
            ],
            BalanceDeltas = new Dictionary<string, long>(),
            Memos = []
        };
        var dto = ToDto(sample, static signature => "https://explorer.solana.com/tx/" + signature);
        if (dto.Hash != "sig"
            || dto.LogicalTime != "9"
            || dto.Fee != "0.000005"
            || dto.FeeNano != "5000"
            || dto.Aborted != false
            || dto.Messages.Count != 1
            || dto.Messages[0].Amount != "0.025")
        {
            throw new InvalidOperationException("Transaction mapper self-check failed.");
        }
    }

    public Task<WalletAddressDto> GetAddressAsync()
    {
        return Task.FromResult(_address);
    }

    public async Task<WalletBalanceDto> GetBalanceAsync(CancellationToken cancellationToken)
    {
        var account = await _rpc.GetAccountAsync(_address.Address, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new WalletBalanceDto(
                _address.Address,
                _options.Network,
                "0",
                "0",
                "nonexist",
                false,
                DateTimeOffset.UtcNow);
        }

        _logger.LogDebug("Wallet status is active.");
        return new WalletBalanceDto(
            _address.Address,
            _options.Network,
            SolAmount.FormatSol(account.Lamports),
            account.Lamports.ToString(CultureInfo.InvariantCulture),
            "active",
            true,
            DateTimeOffset.UtcNow);
    }

    public async Task<TransactionPageDto> GetTransactionsAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaxTransactionLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 50.");

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be zero or greater.");

        var (page, hasMore) = await PageSignaturesAsync(limit, offset, "confirmed", cancellationToken).ConfigureAwait(false);
        var transactions = new List<WalletTransactionDto>(page.Count);
        foreach (var item in page)
        {
            var body = await _rpc.GetTransactionAsync(item.Signature, "confirmed", cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                transactions.Add(new WalletTransactionDto(
                    item.Signature,
                    item.Slot.ToString(CultureInfo.InvariantCulture),
                    item.BlockTime,
                    null,
                    null,
                    item.Failed,
                    null,
                    item.ConfirmationStatus,
                    _options.TransactionExplorerUrl(item.Signature),
                    []));
                continue;
            }

            transactions.Add(ToDto(body, _options.TransactionExplorerUrl));
        }

        _logger.LogDebug("Loaded {Count} transactions at offset {Offset}.", transactions.Count, offset);
        return new TransactionPageDto(limit, offset, hasMore, transactions);
    }

    internal async Task<IReadOnlyList<SolanaSignatureInfo>> GetRecentSignaturesAsync(
        int limit,
        string? before,
        string commitment,
        CancellationToken cancellationToken)
    {
        return await _rpc.GetSignaturesAsync(_options.WalletAddress, limit, before, commitment, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SolanaChainTransaction?> GetChainTransactionAsync(
        string signature,
        string commitment,
        CancellationToken cancellationToken)
    {
        return await _rpc.GetTransactionAsync(signature, commitment, cancellationToken).ConfigureAwait(false);
    }

    internal static WalletTransactionDto ToDto(SolanaChainTransaction transaction, Func<string, string> explorerUrl)
    {
        return new WalletTransactionDto(
            transaction.Signature,
            transaction.Slot.ToString(CultureInfo.InvariantCulture),
            transaction.BlockTime,
            SolAmount.FormatSol(transaction.FeeLamports),
            transaction.FeeLamports.ToString(CultureInfo.InvariantCulture),
            transaction.Failed,
            null,
            transaction.Failed ? "failed" : "confirmed",
            explorerUrl(transaction.Signature),
            transaction.Messages);
    }

    private async Task<(List<SolanaSignatureInfo> Items, bool HasMore)> PageSignaturesAsync(
        int limit,
        int offset,
        string commitment,
        CancellationToken cancellationToken)
    {
        var want = limit + 1;
        var skip = offset;
        string? before = null;
        var collected = new List<SolanaSignatureInfo>(want);
        var scanned = 0;
        while (collected.Count < want && scanned < PayoutLimits.MaxSignatureScan)
        {
            var batchSize = Math.Min(50, PayoutLimits.MaxSignatureScan - scanned);
            if (batchSize <= 0)
                break;

            var batch = await _rpc.GetSignaturesAsync(_options.WalletAddress, batchSize, before, commitment, cancellationToken)
                .ConfigureAwait(false);
            if (batch.Count == 0)
                break;

            foreach (var item in batch)
            {
                scanned++;
                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                collected.Add(item);
                if (collected.Count == want)
                    break;
            }

            if (batch.Count < batchSize)
                break;

            before = batch[^1].Signature;
        }

        var hasMore = collected.Count > limit;
        if (hasMore)
            collected.RemoveAt(collected.Count - 1);

        return (collected, hasMore);
    }
}
