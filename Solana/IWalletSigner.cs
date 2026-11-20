namespace SolanaWalletService.Solana;

internal interface IWalletSigner
{
    WalletLocalIdentity Identity { get; }

    SignedTransfer Sign(NativeTransfer transfer);
}

internal sealed record WalletLocalIdentity(bool CanSignLocal, string? Address, string? Problem);

internal readonly record struct ChainCheck(bool Ok, bool Permanent, string? Problem);

internal sealed record NativeTransfer(
    string Destination,
    long AmountLamports,
    string? Memo,
    string RecentBlockhash,
    long LastValidBlockHeight,
    uint ComputeUnitLimit,
    ulong ComputeUnitPriceMicroLamports);

internal sealed record SignedTransfer(
    string Signature,
    string RecentBlockhash,
    long LastValidBlockHeight,
    string TransactionBase64);
