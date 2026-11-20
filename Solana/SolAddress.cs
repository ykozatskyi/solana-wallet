using Solnet.Wallet;

namespace SolanaWalletService.Solana;

internal sealed class SolAddress
{
    public const string SystemProgram = "11111111111111111111111111111111";

    private SolAddress(string address)
    {
        Address = address;
    }

    public string Address { get; }

    public static void SelfCheck()
    {
        const string wrappedSol = "So11111111111111111111111111111111111111112";
        if (!TryParse(wrappedSol, out var parsed)
            || !string.Equals(parsed.Address, wrappedSol, StringComparison.Ordinal)
            || !TryParse(SystemProgram, out _)
            || TryParse("not-an-address", out _)
            || TryParse("", out _)
            || Shorten(wrappedSol).Length >= wrappedSol.Length)
        {
            throw new InvalidOperationException("Solana address parser failed its self-check.");
        }
    }

    public static SolAddress Parse(string value)
    {
        if (!TryParse(value, out var address))
            throw new FormatException("Invalid Solana address.");

        return address;
    }

    public static bool TryParse(string? value, out SolAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.Length is < 32 or > 44)
            return false;

        if (!PublicKey.IsValid(text, validateCurve: false))
            return false;

        try
        {
            var key = new PublicKey(text);
            if (!string.Equals(key.Key, text, StringComparison.Ordinal))
                return false;

            address = new SolAddress(key.Key);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public static string Shorten(string address)
    {
        if (address.Length <= 12)
            return address;

        return string.Concat(address.AsSpan(0, 4), "…", address.AsSpan(address.Length - 4));
    }
}
