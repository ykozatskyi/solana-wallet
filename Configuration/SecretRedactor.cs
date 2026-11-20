using System.Text.RegularExpressions;

namespace SolanaWalletService.Configuration;

internal static partial class SecretRedactor
{
    public static string Redact(string? text, AppOptions options)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var redacted = text;
        redacted = Replace(redacted, options.TelegramBotToken);
        redacted = Replace(redacted, options.ApiKey);
        redacted = Replace(redacted, options.DatabasePassword);
        redacted = KeypairPattern().Replace(redacted, "[***]");
        redacted = Base64Pattern().Replace(redacted, "***");
        return redacted;
    }

    [GeneratedRegex(@"\[\s*\d{1,3}\s*(?:,\s*\d{1,3}\s*){31,}\]", RegexOptions.CultureInvariant)]
    private static partial Regex KeypairPattern();

    [GeneratedRegex(@"[A-Za-z0-9+/]*[+/][A-Za-z0-9+/=]{40,}", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Pattern();

    private static string Replace(string text, string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return text;

        return text.Replace(secret, "***", StringComparison.Ordinal);
    }
}
