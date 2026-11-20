using System.Globalization;

namespace SolanaWalletService.Solana;

internal static class SolAmount
{
    public const decimal LamportsPerSol = 1_000_000_000m;

    static SolAmount()
    {
        if (FormatSol("0") != "0"
            || FormatSol("1") != "0.000000001"
            || FormatSol("1000000000") != "1"
            || FormatSol("1500000000") != "1.5"
            || FormatSol("25000000") != "0.025"
            || FormatSol("1592546925409142") != "1592546.925409142")
        {
            throw new InvalidOperationException("SOL amount formatting failed its self-check.");
        }
    }

    public static void SelfCheck()
    {
        _ = FormatSol("1");
        if (!TryParseSol("0.025", out var lamports) || lamports != 25_000_000
            || TryParseSol("0.0000000001", out _)
            || TryParseSol("-1", out _)
            || TryParseSol("01", out _))
        {
            throw new InvalidOperationException("SOL amount parsing failed its self-check.");
        }
    }

    public static bool TryNormalizeLamports(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length > 40)
            return false;

        if (!decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return false;

        normalized = value.ToString("0", CultureInfo.InvariantCulture);
        return true;
    }

    public static bool TryParseSol(string? text, out long lamports)
    {
        lamports = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > 40)
            return false;

        if (trimmed[0] is '+' or '-' || trimmed.Contains('_') || trimmed.Contains(',')
            || trimmed.Contains('e') || trimmed.Contains('E') || trimmed.Contains(' '))
        {
            return false;
        }

        var dot = trimmed.IndexOf('.');
        if (dot != trimmed.LastIndexOf('.'))
            return false;

        if (dot == 0 || dot == trimmed.Length - 1)
            return false;

        var whole = dot < 0 ? trimmed : trimmed[..dot];
        var fraction = dot < 0 ? string.Empty : trimmed[(dot + 1)..];
        if (whole.Length == 0 || fraction.Length > 9)
            return false;

        if (!whole.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit))
            return false;

        if (whole.Length > 1 && whole[0] == '0')
            return false;

        if (!ulong.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var wholeUnits))
            return false;

        var fractionLamports = 0u;
        if (fraction.Length > 0
            && !uint.TryParse(fraction.PadRight(9, '0'), NumberStyles.None, CultureInfo.InvariantCulture, out fractionLamports))
        {
            return false;
        }

        if (wholeUnits > (ulong)long.MaxValue / 1_000_000_000UL)
            return false;

        var total = (wholeUnits * 1_000_000_000UL) + fractionLamports;
        if (total > long.MaxValue)
            return false;

        lamports = (long)total;
        return true;
    }

    public static string FormatSol(string lamportsAmount)
    {
        if (!TryNormalizeLamports(lamportsAmount, out var normalized))
            throw new FormatException("Lamport amount is not an integer.");

        var lamports = decimal.Parse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.Integer, CultureInfo.InvariantCulture);
        var sol = decimal.Divide(lamports, LamportsPerSol);
        return sol.ToString("0.#########", CultureInfo.InvariantCulture);
    }

    public static string FormatSol(long lamports)
    {
        return FormatSol(lamports.ToString(CultureInfo.InvariantCulture));
    }
}
