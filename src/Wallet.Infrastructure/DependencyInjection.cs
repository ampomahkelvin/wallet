using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wallet.Infrastructure.Accounts;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Messaging;
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

        var rabbitMqHost = config.GetConnectionString("RabbitMQ") ?? "amqp://guest:guest@localhost:5672/";
        services.AddSingleton<IConnection>(_ =>
        {
            var factory = new ConnectionFactory { Uri = new Uri(rabbitMqHost), DispatchConsumersAsync = true };
            return factory.CreateConnection();
        });
        services.AddSingleton<EventDispatcher>(sp =>
        {
            var dispatcher = new EventDispatcher(sp.GetRequiredService<ILogger<EventDispatcher>>());
            dispatcher.Register("transfer.completed", _ => Task.CompletedTask);
            dispatcher.Register("deposit.completed", _ => Task.CompletedTask);
            dispatcher.Register("withdrawal.completed", _ => Task.CompletedTask);
            dispatcher.Register("transfer.reversed", _ => Task.CompletedTask);
            return dispatcher;
        });
        services.Configure<RelayOptions>(config.GetSection("Relay"));
        services.AddHostedService<RelayService>();
        services.AddHostedService<ConsumerService>();

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        await db.Database.MigrateAsync();
    }
}