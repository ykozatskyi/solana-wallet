using System.Text.Json;
using SolanaWalletService.Configuration;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace SolanaWalletService.Solana;

internal sealed class SolanaWalletSigner : IWalletSigner
{
    private readonly Account? _account;
    private readonly ILogger<SolanaWalletSigner> _logger;

    private SolanaWalletSigner(WalletLocalIdentity identity, Account? account, ILogger<SolanaWalletSigner> logger)
    {
        Identity = identity;
        _account = account;
        _logger = logger;
    }

    public WalletLocalIdentity Identity { get; }

    public static SolanaWalletSigner Create(AppOptions options, ILogger<SolanaWalletSigner> logger)
    {
        if (options.KeypairProblem is not null)
            return Disabled(options.KeypairProblem, logger);

        if (string.IsNullOrWhiteSpace(options.KeypairPath))
        {
            const string reason = "SOLANA_KEYPAIR_PATH is not configured.";
            logger.LogInformation("Payout signing is disabled: {Reason}", reason);
            return Disabled(reason, logger);
        }

        try
        {
            if (!File.Exists(options.KeypairPath))
                return Disabled("Solana keypair file was not found.", logger);

            var info = new FileInfo(options.KeypairPath);
            if (info.Length is < 64 or > 8_192)
                return Disabled("Solana keypair file is not a Solana CLI keypair.", logger);

            var secret = ReadCliKeypair(info.FullName);
            if (secret is null)
                return Disabled("Solana keypair file is not a 64-byte CLI keypair.", logger);

            var account = new Account(secret, secret.AsSpan(32, 32).ToArray());
            if (!string.Equals(account.PublicKey.Key, options.WalletAddress, StringComparison.Ordinal))
            {
                logger.LogError("Solana keypair does not match SOLANA_WALLET_ADDRESS {Address}.", SolAddress.Shorten(options.WalletAddress));
                return Disabled("Solana keypair does not match SOLANA_WALLET_ADDRESS.", logger);
            }

            logger.LogInformation(
                "Solana keypair matches {Address}. Chain check runs before signing.",
                SolAddress.Shorten(account.PublicKey.Key));
            return new SolanaWalletSigner(new WalletLocalIdentity(true, account.PublicKey.Key, null), account, logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        {
            logger.LogError("Payout signing is disabled ({Type}).", ex.GetType().Name);
            return Disabled("Solana keypair could not be read.", logger);
        }
    }

    public SignedTransfer Sign(NativeTransfer transfer)
    {
        if (!Identity.CanSignLocal || _account is null)
            throw new InvalidOperationException("Signing is disabled.");

        if (transfer.AmountLamports <= 0)
            throw new ArgumentOutOfRangeException(nameof(transfer), "Amount must be positive.");

        if (transfer.LastValidBlockHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(transfer), "Blockhash expiry is missing.");

        if (!PublicKey.IsValid(transfer.RecentBlockhash, validateCurve: false))
            throw new InvalidOperationException("Recent blockhash is invalid.");

        if (!SolAddress.TryParse(transfer.Destination, out var destination))
            throw new InvalidOperationException("Destination is invalid.");

        var builder = new TransactionBuilder()
            .SetRecentBlockHash(transfer.RecentBlockhash)
            .SetFeePayer(_account)
            .AddInstruction(ComputeBudgetProgram.SetComputeUnitLimit(transfer.ComputeUnitLimit));

        if (transfer.ComputeUnitPriceMicroLamports > 0)
            builder.AddInstruction(ComputeBudgetProgram.SetComputeUnitPrice(transfer.ComputeUnitPriceMicroLamports));

        builder.AddInstruction(SystemProgram.Transfer(
            _account.PublicKey,
            new PublicKey(destination.Address),
            (ulong)transfer.AmountLamports));

        if (!string.IsNullOrEmpty(transfer.Memo))
            builder.AddInstruction(MemoProgram.NewMemo(_account.PublicKey, transfer.Memo));

        var wire = builder.Build(_account);
        var signature = EncodeSignature(wire);
        return new SignedTransfer(
            signature,
            transfer.RecentBlockhash,
            transfer.LastValidBlockHeight,
            Convert.ToBase64String(wire));
    }

    private static string EncodeSignature(byte[] transaction)
    {
        if (transaction.Length < 65 || transaction[0] != 1)
            throw new InvalidOperationException("Signed transaction is not a single-signer transaction.");

        var signature = new byte[64];
        transaction.AsSpan(1, 64).CopyTo(signature);
        return new Base58Encoder().EncodeData(signature);
    }

    private static byte[]? ReadCliKeypair(string path)
    {
        using var stream = File.OpenRead(path);
        var numbers = JsonSerializer.Deserialize<int[]>(stream);
        if (numbers is not { Length: 64 })
            return null;

        var secret = new byte[64];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (numbers[i] is < 0 or > 255)
            {
                CryptographicClear(secret);
                return null;
            }

            secret[i] = (byte)numbers[i];
        }

        return secret;
    }

    private static void CryptographicClear(byte[] secret)
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
    }

    private static SolanaWalletSigner Disabled(string reason, ILogger<SolanaWalletSigner> logger)
    {
        logger.LogWarning("Payout signing is disabled: {Reason}", reason);
        return new SolanaWalletSigner(new WalletLocalIdentity(false, null, reason), null, logger);
    }
}
