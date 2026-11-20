using Microsoft.EntityFrameworkCore;
using Npgsql;
using SolanaWalletService.Persistence;

namespace SolanaWalletService.Services;

internal sealed class DepositStore
{
    private readonly IDbContextFactory<WalletDbContext> _factory;

    public DepositStore(IDbContextFactory<WalletDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<DepositCursorEntity?> GetCursorAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DepositCursors.AsNoTracking()
            .SingleOrDefaultAsync(cursor => cursor.Id == 1, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryArmAsync(string? signature, long slot, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.DepositCursors.AnyAsync(cursor => cursor.Id == 1, cancellationToken).ConfigureAwait(false))
            return false;

        db.DepositCursors.Add(new DepositCursorEntity
        {
            Id = 1,
            Signature = signature,
            Slot = slot
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    public async Task AdvanceAsync(string signature, long slot, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cursor = await db.DepositCursors.SingleOrDefaultAsync(row => row.Id == 1, cancellationToken).ConfigureAwait(false);
        if (cursor is null)
        {
            db.DepositCursors.Add(new DepositCursorEntity
            {
                Id = 1,
                Signature = signature,
                Slot = slot
            });
        }
        else
        {
            cursor.Signature = signature;
            cursor.Slot = slot;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another pass armed the cursor first.
        }
    }

    public async Task<bool> TryRecordAsync(
        string signature,
        long slot,
        long amountLamports,
        string? fromAddress,
        DateTimeOffset seenAtUtc,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.DepositNotifications.Add(new DepositNotificationEntity
        {
            Signature = signature,
            Slot = slot,
            AmountLamports = amountLamports,
            FromAddress = fromAddress,
            SeenAtUtc = seenAtUtc
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation)
                return true;
        }

        return false;
    }
}
