using System.Globalization;
using System.Text.Json;

namespace SolanaWalletService.Services;

internal sealed class SolUsdtQuote
{
    public const string HttpClientName = "binance";

    private const string Endpoint = "https://data-api.binance.vision/api/v3/ticker/price?symbol=SOLUSDT";
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<SolUsdtQuote> _logger;
    private readonly object _gate = new();
    private decimal? _price;
    private DateTimeOffset _fetchedAt;

    public SolUsdtQuote(IHttpClientFactory httpClientFactory, TimeProvider time, ILogger<SolUsdtQuote> logger)
    {
        _httpClientFactory = httpClientFactory;
        _time = time;
        _logger = logger;
    }

    public static void SelfCheck()
    {
        if (!TryReadPrice("{\"symbol\":\"SOLUSDT\",\"price\":\"140.5\"}", out var price) || price != 140.5m
            || TryReadPrice("{\"price\":\"0\"}", out _)
            || TryReadPrice("{}", out _)
            || TryReadPrice("nope", out _)
            || Format("1.5", 100m) != "150.00"
            || Format("0.025", 140.5m) != "3.51"
            || Format("0", 200m) != "0.00"
            || Format("1", 1.005m) != "1.01"
            || Format("1", null) is not null
            || Format("nope", 1m) is not null)
        {
            throw new InvalidOperationException("SOL/USDT quote self-check failed.");
        }
    }

    public async Task<decimal?> TryGetAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_price is decimal cached && now - _fetchedAt < FreshFor)
                return cached;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(Endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SOL/USDT price lookup returned {Status}.", (int)response.StatusCode);
                return Cached();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 256 || !TryReadPrice(body, out var price))
            {
                _logger.LogWarning("SOL/USDT price lookup returned an unreadable price.");
                return Cached();
            }

            lock (_gate)
            {
                _price = price;
                _fetchedAt = _time.GetUtcNow();
            }

            return price;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("SOL/USDT price lookup failed ({Type}).", ex.GetType().Name);
            return Cached();
        }
    }

    public static string? Format(string? solText, decimal? price)
    {
        if (price is not decimal rate || rate <= 0 || string.IsNullOrWhiteSpace(solText))
            return null;

        if (!decimal.TryParse(solText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sol) || sol < 0)
            return null;

        var usdt = decimal.Round(sol * rate, 2, MidpointRounding.AwayFromZero);
        return usdt.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private decimal? Cached()
    {
        lock (_gate)
            return _price;
    }

    private static bool TryReadPrice(string json, out decimal price)
    {
        price = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("price", out var element))
                return false;

            var text = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null
            };

            return text is not null
                && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out price)
                && price > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
