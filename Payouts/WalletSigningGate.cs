using SolanaWalletService.Solana;

namespace SolanaWalletService.Payouts;

internal sealed class WalletSigningGate
{
    private readonly object _gate = new();

    public bool LocalOk { get; private set; }

    public bool ChainOk { get; private set; }

    public bool PermanentlyBlocked { get; private set; }

    public string? Problem { get; private set; }

    public bool CanQueue
    {
        get
        {
            lock (_gate)
                return LocalOk && !PermanentlyBlocked;
        }
    }

    public bool CanSign
    {
        get
        {
            lock (_gate)
                return LocalOk && ChainOk && !PermanentlyBlocked;
        }
    }

    public void ApplyLocal(WalletLocalIdentity identity)
    {
        lock (_gate)
        {
            LocalOk = identity.CanSignLocal;
            Problem = identity.Problem;
            if (!LocalOk)
                PermanentlyBlocked = true;
        }
    }

    public void ApplyChain(ChainCheck check)
    {
        lock (_gate)
        {
            if (PermanentlyBlocked)
                return;

            if (check.Ok)
            {
                ChainOk = true;
                Problem = null;
                return;
            }

            if (check.Permanent)
            {
                ChainOk = false;
                PermanentlyBlocked = true;
                Problem = check.Problem;
                return;
            }

            if (!ChainOk)
                Problem = check.Problem;
        }
    }
}
