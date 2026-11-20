using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SolanaWalletService.Persistence;

namespace SolanaWalletService.Payouts;

internal enum PayoutAddResult
{
    Added,
    Duplicate,
    Insufficient
}

internal sealed class PayoutStore
{
    private readonly IDbContextFactory<WalletDbContext> _factory;
    private readonly TimeProvider _time;

    public PayoutStore(IDbContextFactory<WalletDbContext> factory, TimeProvider time)
    {
        _factory = factory;
        _time = time;
    }

    public async Task<WalletLease> AcquireWalletLeaseAsync(CancellationToken cancellationToken)
    {
        var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockWalletAsync(db, cancellationToken).ConfigureAwait(false);
            return new WalletLease(db, transaction);
        }
        catch
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);

            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken)
    {
        return ReadSettingsAsync(db => db.PayoutSettings.AsNoTracking(), settings => settings.Paused, cancellationToken);
    }

    public async Task<bool> IsPausedAsync(WalletLease lease, CancellationToken cancellationToken)
    {
        var settings = await RequiredSettingsAsync(lease.Db, tracking: false, cancellationToken).ConfigureAwait(false);
        return settings.Paused;
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await RequiredSettingsAsync(db, tracking: true, cancellationToken).ConfigureAwait(false);
        settings.Paused = paused;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> NotifyOnSuccessAsync(CancellationToken cancellationToken)
    {
        return ReadSettingsAsync(db => db.PayoutSettings.AsNoTracking(), settings => settings.NotifyOnSuccess, cancellationToken);
    }

    public async Task<bool> ToggleNotifyOnSuccessAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({PostgresLocks.Settings})",
            cancellationToken).ConfigureAwait(false);
        var settings = await RequiredSettingsAsync(db, tracking: true, cancellationToken).ConfigureAwait(false);
        settings.NotifyOnSuccess = !settings.NotifyOnSuccess;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return settings.NotifyOnSuccess;
    }

    public async Task<(bool Paused, bool NotifyOnSuccess)> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await RequiredSettingsAsync(db, tracking: false, cancellationToken).ConfigureAwait(false);
        return (settings.Paused, settings.NotifyOnSuccess);
    }

    public async Task<(int Queued, int NeedsReview)> CountsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var queued = await db.Payouts.CountAsync(payout => payout.Status == PayoutStatus.Queued, cancellationToken).ConfigureAwait(false);
        var review = await db.Payouts.CountAsync(payout => payout.Status == PayoutStatus.NeedsReview, cancellationToken).ConfigureAwait(false);
        return (queued, review);
    }

    public async Task<PayoutRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await Tracked(db.Payouts.AsNoTracking())
            .FirstOrDefaultAsync(payout => payout.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : PayoutRecordMap.ToRecord(entity);
    }

    public async Task<PayoutRecord?> FindByRequestAsync(string requestId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await Tracked(db.Payouts.AsNoTracking())
            .FirstOrDefaultAsync(payout => payout.RequestId == requestId, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : PayoutRecordMap.ToRecord(entity);
    }

    public async Task<PayoutRecord?> ReadAsync(WalletLease lease, Guid id, CancellationToken cancellationToken)
    {
        var entity = await Tracked(lease.Db.Payouts.AsNoTracking())
            .FirstOrDefaultAsync(payout => payout.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : PayoutRecordMap.ToRecord(entity);
    }

    public async Task<PayoutAddResult> TryAddIfAffordableAsync(
        PayoutRecord payout,
        Func<long, bool> canAfford,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockWalletAsync(db, cancellationToken).ConfigureAwait(false);

        var exists = await db.Payouts.AsNoTracking()
            .AnyAsync(row => row.RequestId == payout.RequestId || row.Id == payout.Id, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
            return PayoutAddResult.Duplicate;

        var reserved = await ReservedLamportsAsync(db, exceptId: null, cancellationToken).ConfigureAwait(false);
        if (!canAfford(reserved))
            return PayoutAddResult.Insufficient;

        db.Payouts.Add(PayoutRecordMap.ToEntity(payout));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PayoutAddResult.Added;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // PostgreSQL already aborted the transaction.
            }

            return PayoutAddResult.Duplicate;
        }
    }

    public async Task UpdateAsync(Guid id, Action<PayoutRecord> change, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entity = await LockAndLoadAsync(db, id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
            return;

        Apply(entity, change);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(WalletLease lease, Guid id, Action<PayoutRecord> change, CancellationToken cancellationToken)
    {
        var entity = await LockAndLoadAsync(lease.Db, id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
            return false;

        Apply(entity, change);
        await lease.Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<PayoutRecord?> NextQueuedAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await Tracked(db.Payouts.AsNoTracking())
            .Where(payout => payout.Status == PayoutStatus.Queued)
            .OrderBy(payout => payout.CreatedAtUtc)
            .ThenBy(payout => payout.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return entity is null ? null : PayoutRecordMap.ToRecord(entity);
    }

    public async Task<IReadOnlyList<PayoutRecord>> InFlightAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await Tracked(InFlight(db.Payouts.AsNoTracking()))
            .OrderBy(payout => payout.CreatedAtUtc)
            .ThenBy(payout => payout.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(PayoutRecordMap.ToRecord).ToList();
    }

    public async Task<bool> HasInFlightAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await InFlight(db.Payouts).AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HasInFlightAsync(WalletLease lease, CancellationToken cancellationToken)
    {
        return InFlight(lease.Db.Payouts).AnyAsync(cancellationToken);
    }

    public async Task<bool> HasNeedsReviewAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Payouts.AnyAsync(payout => payout.Status == PayoutStatus.NeedsReview, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HasNeedsReviewAsync(WalletLease lease, CancellationToken cancellationToken)
    {
        return lease.Db.Payouts.AnyAsync(payout => payout.Status == PayoutStatus.NeedsReview, cancellationToken);
    }

    public async Task<(IReadOnlyList<PayoutRecord> Items, bool HasMore)> ListAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Payouts.AsNoTracking()
            .OrderByDescending(payout => payout.CreatedAtUtc)
            .ThenByDescending(payout => payout.Id)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = rows.Count > limit;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        return (rows.Select(PayoutRecordMap.ToRecord).ToList(), hasMore);
    }

    public Task<long> ReservedLamportsAsync(WalletLease lease, Guid? exceptId, CancellationToken cancellationToken)
    {
        return ReservedLamportsAsync(lease.Db, exceptId, cancellationToken);
    }

    private void Apply(PayoutEntity entity, Action<PayoutRecord> change)
    {
        var record = PayoutRecordMap.ToRecord(entity);
        change(record);
        record.UpdatedAtUtc = _time.GetUtcNow();
        PayoutRecordMap.Copy(entity, record, replaceSignatures: false);
    }

    private async Task<long> ReservedLamportsAsync(WalletDbContext db, Guid? exceptId, CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow().AddSeconds(-PayoutLimits.ConfirmHoldSeconds);
        var query = db.Payouts.AsNoTracking().Where(payout =>
            payout.Status == PayoutStatus.Queued
            || payout.Status == PayoutStatus.Signed
            || payout.Status == PayoutStatus.Broadcasted
            || payout.Status == PayoutStatus.NeedsReview
            || (payout.Status == PayoutStatus.Confirmed
                && payout.ConfirmedAtUtc != null
                && payout.ConfirmedAtUtc > cutoff));

        if (exceptId is Guid skip)
            query = query.Where(payout => payout.Id != skip);

        var lines = await query
            .Select(payout => payout.AmountLamports + payout.FeeReserveLamports)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        long reserved = 0;
        foreach (var line in lines)
        {
            if (line < 0 || reserved > long.MaxValue - line)
                return long.MaxValue;

            reserved += line;
        }

        return reserved;
    }

    private async Task<T> ReadSettingsAsync<T>(
        Func<WalletDbContext, IQueryable<PayoutSettingsEntity>> query,
        Func<PayoutSettingsEntity, T> select,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await RequiredSettingsAsync(query(db), cancellationToken).ConfigureAwait(false);
        return select(settings);
    }

    private static async Task<PayoutEntity?> LockAndLoadAsync(WalletDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var (hi, lo) = PostgresLocks.Payout(id);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({hi}, {lo})",
            cancellationToken).ConfigureAwait(false);
        return await Tracked(db.Payouts)
            .FirstOrDefaultAsync(payout => payout.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task LockWalletAsync(WalletDbContext db, CancellationToken cancellationToken)
    {
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({PostgresLocks.Wallet})",
            cancellationToken);
    }

    private static IQueryable<PayoutEntity> Tracked(IQueryable<PayoutEntity> query)
    {
        return query.Include(payout => payout.Signatures);
    }

    private static IQueryable<PayoutEntity> InFlight(IQueryable<PayoutEntity> query)
    {
        return query.Where(payout => payout.Status == PayoutStatus.Signed || payout.Status == PayoutStatus.Broadcasted);
    }

    private static async Task<PayoutSettingsEntity> RequiredSettingsAsync(
        WalletDbContext db,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking ? db.PayoutSettings : db.PayoutSettings.AsNoTracking();
        return await RequiredSettingsAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PayoutSettingsEntity> RequiredSettingsAsync(
        IQueryable<PayoutSettingsEntity> query,
        CancellationToken cancellationToken)
    {
        var settings = await query.SingleOrDefaultAsync(row => row.Id == 1, cancellationToken).ConfigureAwait(false);
        if (settings is null)
            throw new InvalidOperationException("Payout settings are missing from PostgreSQL.");

        return settings;
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
