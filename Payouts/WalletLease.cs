using SolanaWalletService.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace SolanaWalletService.Payouts;

internal sealed class WalletLease : IAsyncDisposable
{
    private readonly WalletDbContext _db;
    private readonly IDbContextTransaction _transaction;
    private bool _finished;

    internal WalletLease(WalletDbContext db, IDbContextTransaction transaction)
    {
        _db = db;
        _transaction = transaction;
    }

    internal WalletDbContext Db => _db;

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_finished)
            return;

        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_finished)
                await _transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The transaction is already finished or the connection is gone.
        }

        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _db.DisposeAsync().ConfigureAwait(false);
    }
}
