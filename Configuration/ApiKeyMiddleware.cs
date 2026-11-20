using System.Security.Cryptography;
using System.Text;
using SolanaWalletService.Models;

namespace SolanaWalletService.Configuration;

internal sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly AppOptions _options;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, AppOptions options, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var values)
            || values.Count != 1
            || !KeysMatch(values.ToString().Trim(), _options.ApiKey))
        {
            _logger.LogInformation("Rejected an API request without a valid key.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Unauthorized."), context.RequestAborted);
            return;
        }

        await _next(context);
    }

    private static bool KeysMatch(string provided, string expected)
    {
        if (provided.Length is < 1 or > 256)
            return false;

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }
}
