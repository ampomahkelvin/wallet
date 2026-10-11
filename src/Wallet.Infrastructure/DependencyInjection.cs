using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wallet.Infrastructure.Accounts;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Transfers;

namespace Wallet.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");

        services.AddDbContextFactory<WalletDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<WalletDbContext>(sp => sp.GetRequiredService<IDbContextFactory<WalletDbContext>>().CreateDbContext());

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ITransferService, TransferService>();
        services.AddScoped<IIdempotencyService, IdempotencyService>();

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        await db.Database.MigrateAsync();
    }
}