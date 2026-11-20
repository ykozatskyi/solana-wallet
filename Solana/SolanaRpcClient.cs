using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SolanaWalletService.Configuration;
using SolanaWalletService.Models;

namespace SolanaWalletService.Solana;

internal sealed class SolanaRpcClient
{
    public const string HttpClientName = "solana-rpc";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppOptions _options;
    private readonly ILogger<SolanaRpcClient> _logger;
    private int _nextId = 1;

    public SolanaRpcClient(IHttpClientFactory httpClientFactory, AppOptions options, ILogger<SolanaRpcClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<long> GetBalanceAsync(string address, CancellationToken cancellationToken)
    {
        using var result = await CallAsync(
            "getBalance",
            Params(address, CommitmentObject("confirmed")),
            sending: false,
            cancellationToken).ConfigureAwait(false);

        if (!TryContextValue(result.RootElement, out var value) || !TryLamports(value, out var lamports))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable balance.");

        return lamports;
    }

    public async Task<SolanaAccount?> GetAccountAsync(string address, CancellationToken cancellationToken)
    {
        using var result = await CallAsync(
            "getAccountInfo",
            Params(address, new { encoding = "base64", commitment = "confirmed" }),
            sending: false,
            cancellationToken).ConfigureAwait(false);

        if (!TryContextValue(result.RootElement, out var value))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected account response.");

        if (value.ValueKind is JsonValueKind.Null)
            return null;

        if (!TryLamports(Require(value, "lamports"), out var lamports))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable balance.");

        var owner = Require(value, "owner").GetString();
        if (!SolAddress.TryParse(owner, out var ownerAddress))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable account owner.");

        if (!value.TryGetProperty("executable", out var executable) || executable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable account.");

        var space = 0;
        if (value.TryGetProperty("space", out var spaceElement))
        {
            if (!spaceElement.TryGetInt32(out space) || space < 0)
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable account.");
        }
        else if (value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            var encoded = data[0].GetString();
            space = string.IsNullOrEmpty(encoded) ? 0 : 1;
        }

        return new SolanaAccount(lamports, ownerAddress.Address, executable.GetBoolean(), space);
    }

    public async Task<SolanaBlockhash> GetLatestBlockhashAsync(CancellationToken cancellationToken)
    {
        using var result = await CallAsync(
            "getLatestBlockhash",
            Params(CommitmentObject("confirmed")),
            sending: false,
            cancellationToken).ConfigureAwait(false);

        if (!TryContextValue(result.RootElement, out var value))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected blockhash response.");

        var blockhash = value.TryGetProperty("blockhash", out var hashElement) ? hashElement.GetString() : null;
        if (!SolAddress.TryParse(blockhash, out _) && !IsBlockhash(blockhash))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable blockhash.");

        if (!value.TryGetProperty("lastValidBlockHeight", out var heightElement) || !TryLong(heightElement, out var height) || height <= 0)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable block height.");

        return new SolanaBlockhash(blockhash!, height);
    }

    public async Task<long> GetBlockHeightAsync(string commitment, CancellationToken cancellationToken)
    {
        using var result = await CallAsync(
            "getBlockHeight",
            Params(CommitmentObject(commitment)),
            sending: false,
            cancellationToken).ConfigureAwait(false);

        if (!TryLong(result.RootElement, out var height) || height < 0)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable block height.");

        return height;
    }

    public async Task<long> GetRentExemptMinimumAsync(CancellationToken cancellationToken)
    {
        using var result = await CallAsync(
            "getMinimumBalanceForRentExemption",
            Params(0, CommitmentObject("confirmed")),
            sending: false,
            cancellationToken).ConfigureAwait(false);

        if (!TryLong(result.RootElement, out var lamports) || lamports <= 0)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable rent exemption.");

        return lamports;
    }

    public async Task<string> SendTransactionAsync(string transactionBase64, bool skipPreflight, CancellationToken cancellationToken)
    {
        using var result = await CallAsync(
            "sendTransaction",
            Params(transactionBase64, new
            {
                encoding = "base64",
                skipPreflight,
                preflightCommitment = "confirmed",
                maxRetries = 0
            }),
            sending: true,
            cancellationToken).ConfigureAwait(false);

        var signature = result.RootElement.GetString();
        if (!IsSignature(signature))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable signature.");

        return signature!;
    }

    public async Task<IReadOnlyList<SolanaSignatureStatus>> GetSignatureStatusesAsync(
        IReadOnlyList<string> signatures,
        CancellationToken cancellationToken)
    {
        if (signatures.Count == 0)
            return [];

        using var result = await CallAsync(
            "getSignatureStatuses",
            Params(signatures, new { searchTransactionHistory = true }),
            sending: false,
            cancellationToken).ConfigureAwait(false);

        if (!TryContextValue(result.RootElement, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected signature status.");

        if (value.GetArrayLength() != signatures.Count)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected signature status.");

        var statuses = new List<SolanaSignatureStatus>(signatures.Count);
        foreach (var item in value.EnumerateArray())
            statuses.Add(ReadStatus(item));

        return statuses;
    }

    public async Task<IReadOnlyList<SolanaSignatureInfo>> GetSignaturesAsync(
        string address,
        int limit,
        string? before,
        string commitment,
        CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, object?>
        {
            ["limit"] = limit,
            ["commitment"] = commitment
        };
        if (!string.IsNullOrWhiteSpace(before))
            config["before"] = before;

        using var result = await CallAsync(
            "getSignaturesForAddress",
            Params(address, config),
            sending: false,
            cancellationToken).ConfigureAwait(false);

        if (result.RootElement.ValueKind != JsonValueKind.Array)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected signature list.");

        var items = new List<SolanaSignatureInfo>();
        foreach (var item in result.RootElement.EnumerateArray())
        {
            var signature = item.TryGetProperty("signature", out var signatureElement) ? signatureElement.GetString() : null;
            if (!IsSignature(signature))
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned a signature list entry without a signature.");

            if (!item.TryGetProperty("slot", out var slotElement) || !TryLong(slotElement, out var slot) || slot < 0)
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable slot.");

            DateTimeOffset? blockTime = null;
            if (item.TryGetProperty("blockTime", out var timeElement) && timeElement.ValueKind == JsonValueKind.Number)
            {
                if (!timeElement.TryGetInt64(out var unix) || unix < 0)
                    throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable timestamp.");

                blockTime = DateTimeOffset.FromUnixTimeSeconds(unix);
            }

            var failed = item.TryGetProperty("err", out var err) && err.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
            var confirmation = item.TryGetProperty("confirmationStatus", out var confirmationElement)
                ? confirmationElement.GetString()
                : null;
            items.Add(new SolanaSignatureInfo(signature!, slot, blockTime, failed, confirmation));
        }

        return items;
    }

    public async Task<SolanaChainTransaction?> GetTransactionAsync(
        string signature,
        string commitment,
        CancellationToken cancellationToken)
    {
        using var result = await CallAsync(
            "getTransaction",
            Params(signature, new
            {
                encoding = "jsonParsed",
                commitment,
                maxSupportedTransactionVersion = 0
            }),
            sending: false,
            cancellationToken).ConfigureAwait(false);

        if (result.RootElement.ValueKind is JsonValueKind.Null)
            return null;

        return SolanaTransactionReader.Read(result.RootElement, signature, _options.WalletAddress);
    }

    private async Task<JsonDocument> CallAsync(string method, object parameters, bool sending, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.RpcUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SolanaRpcException(SolanaRpcErrorKind.Timeout, "Solana RPC timed out.");
        }
        catch (HttpRequestException)
        {
            throw new SolanaRpcException(SolanaRpcErrorKind.Unavailable, "Solana RPC is unavailable.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Solana RPC rate limited {Method}.", method);
                throw new SolanaRpcException(SolanaRpcErrorKind.RateLimited, "Solana RPC is rate limiting requests. Try again shortly.");
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == 0)
                throw new SolanaRpcException(SolanaRpcErrorKind.Unavailable, "Solana RPC is unavailable.");

            if (string.IsNullOrWhiteSpace(body))
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an empty response.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected response.");
            }

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                document.Dispose();
                throw MapError(method, error, sending);
            }

            if (!document.RootElement.TryGetProperty("result", out var result))
            {
                document.Dispose();
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected response.");
            }

            var extracted = JsonDocument.Parse(result.GetRawText());
            document.Dispose();
            return extracted;
        }
    }

    private SolanaRpcException MapError(string method, JsonElement error, bool sending)
    {
        var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed) ? parsed : 0;
        var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "" : "";
        var cleaned = Clean(message);

        if (code == 429 || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Solana RPC rate limited {Method}.", method);
            return new SolanaRpcException(SolanaRpcErrorKind.RateLimited, "Solana RPC is rate limiting requests. Try again shortly.");
        }

        if (message.Contains("already been processed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AlreadyProcessed", StringComparison.Ordinal))
        {
            return new SolanaRpcException(SolanaRpcErrorKind.Duplicate, "Solana RPC already has this transaction.");
        }

        if (sending)
        {
            _logger.LogWarning("Solana RPC rejected {Method} ({Code}).", method, code);
            return new SolanaRpcException(SolanaRpcErrorKind.Rejected, string.IsNullOrWhiteSpace(cleaned)
                ? "Solana RPC rejected the transaction."
                : cleaned);
        }

        if (code is -32005 or -32004 || message.Contains("behind", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Solana RPC is unavailable for {Method} ({Code}).", method, code);
            return new SolanaRpcException(SolanaRpcErrorKind.Unavailable, "Solana RPC is unavailable.");
        }

        _logger.LogWarning("Solana RPC returned an error for {Method} ({Code}).", method, code);
        return new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected response.");
    }

    private static SolanaSignatureStatus ReadStatus(JsonElement item)
    {
        if (item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new SolanaSignatureStatus(false, null, false, null, false);

        var hasError = item.TryGetProperty("err", out var err) && err.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        var error = hasError ? Clean(err.GetRawText()) : null;
        var confirmation = item.TryGetProperty("confirmationStatus", out var confirmationElement)
            && confirmationElement.ValueKind == JsonValueKind.String
            ? confirmationElement.GetString()
            : null;
        var legacyRooted = confirmation is null
            && !hasError
            && item.TryGetProperty("confirmations", out var confirmations)
            && confirmations.ValueKind is JsonValueKind.Null;
        return new SolanaSignatureStatus(true, confirmation, hasError, error, legacyRooted);
    }

    private static bool TryContextValue(JsonElement result, out JsonElement value)
    {
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("value", out value))
            return true;

        value = default;
        return false;
    }

    private static JsonElement Require(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unexpected response.");

        return property;
    }

    private static bool TryLamports(JsonElement element, out long lamports)
    {
        lamports = 0;
        if (!TryUlong(element, out var value) || value > long.MaxValue)
            return false;

        lamports = (long)value;
        return true;
    }

    private static bool TryLong(JsonElement element, out long value)
    {
        value = 0;
        if (!TryUlong(element, out var parsed) || parsed > long.MaxValue)
            return false;

        value = (long)parsed;
        return true;
    }

    private static bool TryUlong(JsonElement element, out ulong value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        if (element.TryGetUInt64(out value))
            return true;

        if (element.TryGetInt64(out var signed) && signed >= 0)
        {
            value = (ulong)signed;
            return true;
        }

        return false;
    }

    private static bool IsSignature(string? signature)
    {
        return !string.IsNullOrWhiteSpace(signature)
            && signature.Length is >= 64 and <= 128
            && signature.All(ch => ch is not ('+' or '/' or '='));
    }

    private static bool IsBlockhash(string? blockhash)
    {
        return !string.IsNullOrWhiteSpace(blockhash)
            && blockhash.Length is >= 32 and <= 44
            && Solnet.Wallet.PublicKey.IsValid(blockhash, validateCurve: false);
    }

    private static string Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        var trimmed = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300];
    }

    private static object CommitmentObject(string commitment)
    {
        return new { commitment };
    }

    private static object?[] Params(params object?[] values)
    {
        return values;
    }
}

internal static class SolanaTransactionReader
{
    public static void SelfCheck()
    {
        const string wallet = "So11111111111111111111111111111111111111112";
        const string other = "11111111111111111111111111111111";
        var json = """
        {
          "slot": 42,
          "blockTime": 1700000000,
          "meta": {
            "err": null,
            "fee": 5000,
            "preBalances": [2000000000, 0],
            "postBalances": [1974995000, 25000000],
            "innerInstructions": []
          },
          "transaction": {
            "message": {
              "accountKeys": [
                {"pubkey": "WALLET", "signer": true, "writable": true},
                {"pubkey": "OTHER", "signer": false, "writable": true}
              ],
              "instructions": [
                {
                  "program": "system",
                  "programId": "11111111111111111111111111111111",
                  "parsed": {"type": "transfer", "info": {"source": "WALLET", "destination": "OTHER", "lamports": 25000000}}
                },
                {"program": "spl-memo", "programId": "MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr", "parsed": "withdrawal-123"}
              ]
            }
          }
        }
        """.Replace("WALLET", wallet, StringComparison.Ordinal).Replace("OTHER", other, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        var tx = Read(document.RootElement, "sig", wallet);
        if (tx.Slot != 42
            || tx.FeeLamports != 5_000
            || tx.Failed
            || tx.Memos.Count != 1
            || tx.Memos[0] != "withdrawal-123"
            || tx.Messages.Count != 1
            || tx.Messages[0].Direction != MessageDirections.Outgoing
            || tx.Messages[0].Amount != "0.025"
            || tx.Messages[0].Counterparty != other
            || !tx.BalanceDeltas.TryGetValue(other, out var credited)
            || credited != 25_000_000
            || tx.BalanceDeltas[wallet] != -25_005_000)
        {
            throw new InvalidOperationException("Solana transaction parser self-check failed.");
        }
    }

    public static SolanaChainTransaction Read(JsonElement result, string signature, string wallet)
    {
        if (!result.TryGetProperty("slot", out var slotElement) || !TryLong(slotElement, out var slot) || slot < 0)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable slot.");

        DateTimeOffset? blockTime = null;
        if (result.TryGetProperty("blockTime", out var timeElement) && timeElement.ValueKind == JsonValueKind.Number)
        {
            if (!timeElement.TryGetInt64(out var unix) || unix < 0)
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable timestamp.");

            blockTime = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        if (!result.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned a transaction without metadata.");

        var failed = meta.TryGetProperty("err", out var err) && err.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        var error = failed ? err.GetRawText() : null;
        if (!meta.TryGetProperty("fee", out var feeElement) || !TryLong(feeElement, out var fee) || fee < 0)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable fee.");

        if (!result.TryGetProperty("transaction", out var transaction)
            || !transaction.TryGetProperty("message", out var message))
        {
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned a transaction without a message.");
        }

        var accounts = ReadAccounts(message);
        var pre = ReadBalances(meta, "preBalances");
        var post = ReadBalances(meta, "postBalances");
        if (pre.Count != post.Count || pre.Count < accounts.Count)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned unreadable balances.");

        var deltas = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < accounts.Count; i++)
        {
            var delta = post[i] - pre[i];
            if (delta != 0)
                deltas[accounts[i]] = delta;
        }

        var memos = new List<string>();
        var messages = new List<WalletMessageDto>();
        if (message.TryGetProperty("instructions", out var instructions))
            ReadInstructions(instructions, wallet, messages, memos);

        if (meta.TryGetProperty("innerInstructions", out var inner) && inner.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in inner.EnumerateArray())
            {
                if (group.TryGetProperty("instructions", out var innerInstructions))
                    ReadInstructions(innerInstructions, wallet, messages, memos);
            }
        }

        if (messages.Count == 0)
            messages.AddRange(MessagesFromDeltas(deltas, wallet));

        return new SolanaChainTransaction
        {
            Signature = signature,
            Slot = slot,
            BlockTime = blockTime,
            Failed = failed,
            Error = error,
            FeeLamports = fee,
            Messages = messages,
            BalanceDeltas = deltas,
            Memos = memos
        };
    }

    private static void ReadInstructions(
        JsonElement instructions,
        string wallet,
        List<WalletMessageDto> messages,
        List<string> memos)
    {
        if (instructions.ValueKind != JsonValueKind.Array)
            return;

        foreach (var instruction in instructions.EnumerateArray())
        {
            var memo = ReadMemo(instruction);
            if (memo is not null)
            {
                memos.Add(memo);
                continue;
            }

            if (!TryTransfer(instruction, out var source, out var destination, out var lamports))
                continue;

            string direction;
            string? counterparty;
            if (string.Equals(source, wallet, StringComparison.Ordinal) && !string.Equals(destination, wallet, StringComparison.Ordinal))
            {
                direction = MessageDirections.Outgoing;
                counterparty = destination;
            }
            else if (string.Equals(destination, wallet, StringComparison.Ordinal) && !string.Equals(source, wallet, StringComparison.Ordinal))
            {
                direction = MessageDirections.Incoming;
                counterparty = source;
            }
            else
            {
                direction = MessageDirections.Other;
                counterparty = string.Equals(source, wallet, StringComparison.Ordinal) ? destination : source;
            }

            messages.Add(new WalletMessageDto(
                direction,
                SolAmount.FormatSol(lamports),
                lamports.ToString(CultureInfo.InvariantCulture),
                counterparty,
                "transfer",
                false));
        }
    }

    private static IEnumerable<WalletMessageDto> MessagesFromDeltas(IReadOnlyDictionary<string, long> deltas, string wallet)
    {
        if (!deltas.TryGetValue(wallet, out var own) || own == 0)
            yield break;

        var direction = own > 0 ? MessageDirections.Incoming : MessageDirections.Outgoing;
        var amount = Math.Abs(own);
        string? counterparty = null;
        foreach (var (address, delta) in deltas)
        {
            if (string.Equals(address, wallet, StringComparison.Ordinal) || delta == 0)
                continue;

            if ((own > 0 && delta < 0) || (own < 0 && delta > 0))
            {
                counterparty = address;
                break;
            }
        }

        yield return new WalletMessageDto(
            direction,
            SolAmount.FormatSol(amount),
            amount.ToString(CultureInfo.InvariantCulture),
            counterparty,
            "transfer",
            false);
    }

    private static string? ReadMemo(JsonElement instruction)
    {
        var program = instruction.TryGetProperty("program", out var programElement) ? programElement.GetString() : null;
        var programId = instruction.TryGetProperty("programId", out var programIdElement) ? programIdElement.GetString() : null;
        var isMemo = string.Equals(program, "spl-memo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(programId, "MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr", StringComparison.Ordinal);
        if (!isMemo || !instruction.TryGetProperty("parsed", out var parsed))
            return null;

        if (parsed.ValueKind == JsonValueKind.String)
            return parsed.GetString();

        if (parsed.ValueKind == JsonValueKind.Object && parsed.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.String)
            return info.GetString();

        return null;
    }

    private static bool TryTransfer(JsonElement instruction, out string source, out string destination, out long lamports)
    {
        source = "";
        destination = "";
        lamports = 0;
        var program = instruction.TryGetProperty("program", out var programElement) ? programElement.GetString() : null;
        var programId = instruction.TryGetProperty("programId", out var programIdElement) ? programIdElement.GetString() : null;
        var isSystem = string.Equals(program, "system", StringComparison.OrdinalIgnoreCase)
            || string.Equals(programId, SolAddress.SystemProgram, StringComparison.Ordinal);
        if (!isSystem || !instruction.TryGetProperty("parsed", out var parsed) || parsed.ValueKind != JsonValueKind.Object)
            return false;

        var type = parsed.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (!string.Equals(type, "transfer", StringComparison.Ordinal))
            return false;

        if (!parsed.TryGetProperty("info", out var info))
            return false;

        source = info.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() ?? "" : "";
        destination = info.TryGetProperty("destination", out var destinationElement) ? destinationElement.GetString() ?? "" : "";
        if (!SolAddress.TryParse(source, out var sourceAddress) || !SolAddress.TryParse(destination, out var destinationAddress))
            return false;

        if (!info.TryGetProperty("lamports", out var lamportsElement) || !TryLong(lamportsElement, out lamports) || lamports < 0)
            return false;

        source = sourceAddress.Address;
        destination = destinationAddress.Address;
        return true;
    }

    private static List<string> ReadAccounts(JsonElement message)
    {
        if (!message.TryGetProperty("accountKeys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned a transaction without accounts.");

        var accounts = new List<string>();
        foreach (var key in keys.EnumerateArray())
        {
            var text = key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : key.TryGetProperty("pubkey", out var pubkey) ? pubkey.GetString() : null;
            if (!SolAddress.TryParse(text, out var address))
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned an unreadable account.");

            accounts.Add(address.Address);
        }

        return accounts;
    }

    private static List<long> ReadBalances(JsonElement meta, string name)
    {
        if (!meta.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned unreadable balances.");

        var balances = new List<long>();
        foreach (var item in array.EnumerateArray())
        {
            if (!TryLong(item, out var value))
                throw new SolanaRpcException(SolanaRpcErrorKind.InvalidResponse, "Solana RPC returned unreadable balances.");

            balances.Add(value);
        }

        return balances;
    }

    private static bool TryLong(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        if (element.TryGetInt64(out value))
            return value >= 0;

        if (element.TryGetUInt64(out var unsigned) && unsigned <= long.MaxValue)
        {
            value = (long)unsigned;
            return true;
        }

        return false;
    }
}
