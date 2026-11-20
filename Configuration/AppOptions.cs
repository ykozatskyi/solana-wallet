using System.Globalization;
using Npgsql;
using SolanaWalletService.Solana;

namespace SolanaWalletService.Configuration;

public sealed class AppOptions
{
    public const long MaxFeeLamports = 1_000_000_000;

    private AppOptions(
        string? telegramBotToken,
        long telegramAdminId,
        string network,
        string rpcUrl,
        string walletAddress,
        string? keypairPath,
        string? keypairProblem,
        string apiKey,
        bool payoutsEnabled,
        long maxPayoutLamports,
        long minReserveLamports,
        long maxPriorityFeeLamports,
        long feeBufferLamports,
        string confirmationCommitment,
        string databaseConnectionString,
        string? databasePassword)
    {
        TelegramBotToken = telegramBotToken;
        TelegramAdminId = telegramAdminId;
        Network = network;
        RpcUrl = rpcUrl;
        WalletAddress = walletAddress;
        KeypairPath = keypairPath;
        KeypairProblem = keypairProblem;
        ApiKey = apiKey;
        PayoutsEnabled = payoutsEnabled;
        MaxPayoutLamports = maxPayoutLamports;
        MinReserveLamports = minReserveLamports;
        MaxPriorityFeeLamports = maxPriorityFeeLamports;
        FeeBufferLamports = feeBufferLamports;
        ConfirmationCommitment = confirmationCommitment;
        DatabaseConnectionString = databaseConnectionString;
        DatabasePassword = databasePassword;
    }

    public string? TelegramBotToken { get; }

    public long TelegramAdminId { get; }

    public string Network { get; }

    public string RpcUrl { get; }

    public string WalletAddress { get; }

    internal string? KeypairPath { get; }

    internal string? KeypairProblem { get; }

    public string ApiKey { get; }

    public bool PayoutsEnabled { get; }

    public long MaxPayoutLamports { get; }

    public long MinReserveLamports { get; }

    public long MaxPriorityFeeLamports { get; }

    public long FeeBufferLamports { get; }

    public string ConfirmationCommitment { get; }

    internal string DatabaseConnectionString { get; }

    internal string? DatabasePassword { get; }

    public long FeeReserveLamports =>
        checked(SolanaLimits.SignatureFeeLamports + MaxPriorityFeeLamports + FeeBufferLamports);

    public string NetworkLabel => Network switch
    {
        "devnet" => "Solana Devnet",
        "testnet" => "Solana Testnet",
        _ => "Solana Mainnet"
    };

    public string ClusterQuery => Network switch
    {
        "devnet" => "?cluster=devnet",
        "testnet" => "?cluster=testnet",
        _ => ""
    };

    public string AddressExplorerUrl => "https://explorer.solana.com/address/" + WalletAddress + ClusterQuery;

    public string TransactionExplorerUrl(string signature)
    {
        return "https://explorer.solana.com/tx/" + Uri.EscapeDataString(signature) + ClusterQuery;
    }

    public static AppOptions Create(IConfiguration configuration)
    {
        var errors = new List<string>();

        var tokenText = configuration["TELEGRAM_BOT_TOKEN"]?.Trim();
        string? token = null;
        if (!string.IsNullOrEmpty(tokenText))
        {
            if (!IsBotToken(tokenText))
                errors.Add("TELEGRAM_BOT_TOKEN is not a bot token.");
            else
                token = tokenText;
        }

        var adminText = configuration["TELEGRAM_ADMIN_ID"]?.Trim();
        var adminId = 0L;
        if (!long.TryParse(adminText, NumberStyles.Integer, CultureInfo.InvariantCulture, out adminId) || adminId <= 0)
            errors.Add("TELEGRAM_ADMIN_ID must be a positive Telegram user id.");

        var network = configuration["SOLANA_NETWORK"]?.Trim().ToLowerInvariant();
        if (network == "mainnet-beta")
            network = "mainnet";

        if (network is not ("mainnet" or "devnet" or "testnet"))
            errors.Add("SOLANA_NETWORK must be mainnet, devnet, or testnet.");

        var rpcUrl = configuration["SOLANA_RPC_URL"]?.Trim();
        if (string.IsNullOrEmpty(rpcUrl)
            || !Uri.TryCreate(rpcUrl, UriKind.Absolute, out var rpcUri)
            || rpcUri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(rpcUri.UserInfo))
        {
            errors.Add("SOLANA_RPC_URL must be an absolute http or https URL.");
            rpcUrl = null;
        }

        string? walletAddress = null;
        var walletText = configuration["SOLANA_WALLET_ADDRESS"]?.Trim();
        if (string.IsNullOrEmpty(walletText))
            errors.Add("SOLANA_WALLET_ADDRESS is missing.");
        else if (!SolAddress.TryParse(walletText, out var parsedWallet))
            errors.Add("SOLANA_WALLET_ADDRESS is not a valid Solana address.");
        else
            walletAddress = parsedWallet.Address;

        var (keypairPath, keypairProblem) = ReadKeypairPath(configuration, errors);

        var apiKey = configuration["API_KEY"]?.Trim();
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length is < 16 or > 256 || apiKey.Any(char.IsWhiteSpace))
            errors.Add("API_KEY must be 16 to 256 characters and is sent as X-Api-Key.");

        if (!TryReadBool(configuration["SOLANA_PAYOUT_ENABLED"], defaultValue: false, out var payoutsEnabled))
            errors.Add("SOLANA_PAYOUT_ENABLED must be true or false.");

        if (!TryReadSol(configuration["SOLANA_MAX_PAYOUT"], defaultLamports: 1_000_000_000L, out var maxPayoutLamports) || maxPayoutLamports <= 0)
            errors.Add("SOLANA_MAX_PAYOUT must be a positive SOL amount.");

        if (!TryReadSol(configuration["SOLANA_MIN_RESERVE"], defaultLamports: 20_000_000L, out var minReserveLamports) || minReserveLamports < 0)
            errors.Add("SOLANA_MIN_RESERVE must be zero or a positive SOL amount.");

        if (!TryReadLamports(configuration["SOLANA_MAX_PRIORITY_FEE_LAMPORTS"], 10_000L, out var maxPriorityFee)
            || maxPriorityFee is < 0 or > MaxFeeLamports)
        {
            errors.Add("SOLANA_MAX_PRIORITY_FEE_LAMPORTS must be an integer from 0 to 1000000000.");
        }

        if (!TryReadLamports(configuration["SOLANA_FEE_BUFFER_LAMPORTS"], 20_000L, out var feeBuffer)
            || feeBuffer is < 0 or > MaxFeeLamports)
        {
            errors.Add("SOLANA_FEE_BUFFER_LAMPORTS must be an integer from 0 to 1000000000.");
        }

        if (!TryReadCommitment(configuration["SOLANA_CONFIRMATION_COMMITMENT"], out var commitment))
            errors.Add("SOLANA_CONFIRMATION_COMMITMENT must be processed, confirmed, or finalized.");

        var (databaseConnectionString, databasePassword) = ReadDatabase(configuration, errors);

        if (errors.Count > 0
            || apiKey is null
            || walletAddress is null
            || rpcUrl is null
            || network is not ("mainnet" or "devnet" or "testnet")
            || databaseConnectionString is null)
        {
            throw new InvalidOperationException("Configuration is invalid:\n- " + string.Join("\n- ", errors));
        }

        return new AppOptions(
            token,
            adminId,
            network,
            rpcUrl,
            walletAddress,
            keypairPath,
            keypairProblem,
            apiKey,
            payoutsEnabled,
            maxPayoutLamports,
            minReserveLamports,
            maxPriorityFee,
            feeBuffer,
            commitment,
            databaseConnectionString,
            databasePassword);
    }

    public override string ToString()
    {
        return "AppOptions(" + Network + ", " + WalletAddress + ")";
    }

    private static (string? Path, string? Problem) ReadKeypairPath(IConfiguration configuration, List<string> errors)
    {
        var text = configuration["SOLANA_KEYPAIR_PATH"]?.Trim();
        if (string.IsNullOrEmpty(text))
            return (null, null);

        if (text.Length > 512)
        {
            errors.Add("SOLANA_KEYPAIR_PATH is too long.");
            return (null, "SOLANA_KEYPAIR_PATH is too long.");
        }

        if (!Path.IsPathRooted(text))
        {
            errors.Add("SOLANA_KEYPAIR_PATH must be an absolute path.");
            return (null, "SOLANA_KEYPAIR_PATH must be an absolute path.");
        }

        return (text, null);
    }

    private static (string? ConnectionString, string? Password) ReadDatabase(IConfiguration configuration, List<string> errors)
    {
        var text = configuration["DATABASE_CONNECTION_STRING"]?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            errors.Add("DATABASE_CONNECTION_STRING is missing.");
            return (null, null);
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(text);
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
            {
                errors.Add("DATABASE_CONNECTION_STRING must include Host and Database.");
                return (null, null);
            }

            if (!string.Equals(builder.Database, "SOLWalletDB", StringComparison.Ordinal))
            {
                errors.Add("DATABASE_CONNECTION_STRING must target database SOLWalletDB.");
                return (null, null);
            }

            var password = string.IsNullOrEmpty(builder.Password) ? null : builder.Password;
            return (text, password);
        }
        catch (ArgumentException)
        {
            errors.Add("DATABASE_CONNECTION_STRING is not a valid PostgreSQL connection string.");
            return (null, null);
        }
    }

    private static bool TryReadBool(string? text, bool defaultValue, out bool value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = defaultValue;
            return true;
        }

        if (bool.TryParse(text.Trim(), out value))
            return true;

        value = defaultValue;
        return false;
    }

    private static bool TryReadSol(string? text, long defaultLamports, out long lamports)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            lamports = defaultLamports;
            return true;
        }

        return SolAmount.TryParseSol(text, out lamports);
    }

    private static bool TryReadLamports(string? text, long defaultValue, out long value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = defaultValue;
            return true;
        }

        return long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadCommitment(string? text, out string commitment)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            commitment = "finalized";
            return true;
        }

        commitment = text.Trim().ToLowerInvariant();
        return commitment is "processed" or "confirmed" or "finalized";
    }

    private static bool IsBotToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
            return false;

        var index = token.IndexOf(':');
        return index is >= 1 and <= 16
            && long.TryParse(token[..index], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
