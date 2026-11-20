namespace SolanaWalletService.Solana;

internal static class WalletIdentity
{
    public static ChainCheck Evaluate(WalletLocalIdentity local, SolanaAccount? account, long balanceLamports)
    {
        if (!local.CanSignLocal)
            return new ChainCheck(false, true, local.Problem ?? "Signing is not configured.");

        if (balanceLamports < 0)
            return new ChainCheck(false, false, "Wallet balance is unreadable.");

        if (account is null)
            return new ChainCheck(true, false, null);

        if (account.Executable)
            return new ChainCheck(false, true, "Wallet account is executable.");

        if (!string.Equals(account.Owner, SolAddress.SystemProgram, StringComparison.Ordinal))
            return new ChainCheck(false, true, "Wallet account is owned by another program.");

        if (account.Space != 0)
            return new ChainCheck(false, true, "Wallet account is not a standard SOL account.");

        if (account.Lamports != balanceLamports)
            return new ChainCheck(false, false, "Wallet balance did not match the account.");

        return new ChainCheck(true, false, null);
    }
}
