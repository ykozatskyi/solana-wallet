using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SolanaWalletService.Configuration;

namespace SolanaWalletService.Persistence;

public sealed class WalletDbContextFactory : IDesignTimeDbContextFactory<WalletDbContext>
{
    public WalletDbContext CreateDbContext(string[] args)
    {
        EnvFileLoader.Load();
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("DATABASE_CONNECTION_STRING is missing.");

        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new WalletDbContext(options);
    }
}
