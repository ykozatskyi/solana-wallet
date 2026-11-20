using System.Text;
using Microsoft.EntityFrameworkCore;
using SolanaWalletService.Payouts;

namespace SolanaWalletService.Persistence;

public sealed class WalletDbContext : DbContext
{
    public WalletDbContext(DbContextOptions<WalletDbContext> options)
        : base(options)
    {
    }

    public DbSet<PayoutEntity> Payouts => Set<PayoutEntity>();

    public DbSet<PayoutTrackedSignatureEntity> PayoutTrackedSignatures => Set<PayoutTrackedSignatureEntity>();

    public DbSet<PayoutSettingsEntity> PayoutSettings => Set<PayoutSettingsEntity>();

    public DbSet<DepositCursorEntity> DepositCursors => Set<DepositCursorEntity>();

    public DbSet<DepositNotificationEntity> DepositNotifications => Set<DepositNotificationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var payout = modelBuilder.Entity<PayoutEntity>();
        payout.ToTable("payouts", table =>
            table.HasCheckConstraint("ck_payouts_amount_positive", "amount_lamports > 0"));
        payout.HasKey(entity => entity.Id);
        payout.Property(entity => entity.Id).ValueGeneratedNever();
        payout.Property(entity => entity.RequestId).HasMaxLength(36).IsRequired();
        payout.Property(entity => entity.DestinationRaw).HasMaxLength(64).IsRequired();
        payout.Property(entity => entity.DestinationFriendly).HasMaxLength(64).IsRequired();
        payout.Property(entity => entity.Comment);
        payout.Property(entity => entity.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        payout.Property(entity => entity.Signature).HasMaxLength(128);
        payout.Property(entity => entity.SignedTransaction);
        payout.Property(entity => entity.RecentBlockhash).HasMaxLength(64);
        payout.Property(entity => entity.LastError);
        payout.Property(entity => entity.LastSend).HasConversion<string>().HasMaxLength(32).IsRequired();
        payout.HasIndex(entity => entity.RequestId)
            .IsUnique()
            .HasDatabaseName("ux_payouts_request_id");
        payout.HasIndex(entity => new { entity.Status, entity.CreatedAtUtc });
        payout.HasIndex(entity => entity.CreatedAtUtc);
        payout.HasMany(entity => entity.Signatures)
            .WithOne(signature => signature.Payout)
            .HasForeignKey(signature => signature.PayoutId)
            .OnDelete(DeleteBehavior.Cascade);

        var signature = modelBuilder.Entity<PayoutTrackedSignatureEntity>();
        signature.ToTable("payout_tracked_signatures");
        signature.HasKey(entity => new { entity.PayoutId, entity.Ordinal });
        signature.Property(entity => entity.Signature).HasMaxLength(128).IsRequired();
        signature.Property(entity => entity.RecentBlockhash).HasMaxLength(64).IsRequired();

        var settings = modelBuilder.Entity<PayoutSettingsEntity>();
        settings.ToTable("payout_settings");
        settings.HasKey(entity => entity.Id);
        settings.Property(entity => entity.Id).ValueGeneratedNever();

        var cursor = modelBuilder.Entity<DepositCursorEntity>();
        cursor.ToTable("deposit_cursors");
        cursor.HasKey(entity => entity.Id);
        cursor.Property(entity => entity.Id).ValueGeneratedNever();
        cursor.Property(entity => entity.Signature).HasMaxLength(128);

        var deposit = modelBuilder.Entity<DepositNotificationEntity>();
        deposit.ToTable("deposit_notifications");
        deposit.HasKey(entity => entity.Signature);
        deposit.Property(entity => entity.Signature).HasMaxLength(128);
        deposit.Property(entity => entity.FromAddress).HasMaxLength(64);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnake(property.Name));
        }
    }

    private static string ToSnake(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsUpper(ch))
            {
                if (i > 0 && builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');

                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
