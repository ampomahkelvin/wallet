using Microsoft.EntityFrameworkCore;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Tests;

public class DbContextFactoryWrapper(string connectionString) : IDbContextFactory<WalletDbContext>
{
    public WalletDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new WalletDbContext(options);
    }
}