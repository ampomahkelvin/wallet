using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using StackExchange.Redis;
using Wallet.Infrastructure.Accounts;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Maintenance;
using Wallet.Infrastructure.Messaging;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.RateLimiting;
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
        services.AddSingleton(sp => new RabbitMqConnectionProvider(
            rabbitMqHost,
            sp.GetRequiredService<ILogger<RabbitMqConnectionProvider>>()));
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
        services.Configure<ConsumerOptions>(config.GetSection("Consumer"));
        services.AddHostedService<ConsumerService>();

        var redisHost = config.GetConnectionString("Redis") ?? "localhost:6379";
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisHost);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        services.Configure<RateLimiterOptions>(config.GetSection("RateLimiter"));
        services.AddSingleton<IRateLimiter>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
            var redis = sp.GetRequiredService<IConnectionMultiplexer>();
            var logger = sp.GetRequiredService<ILogger<RedisRateLimiter>>();
            return new RedisRateLimiter(redis, opts.MaxRequests, TimeSpan.FromSeconds(opts.WindowSeconds), logger);
        });

        services.Configure<MaintenanceOptions>(config.GetSection("Maintenance"));
        services.AddHostedService<MaintenanceService>();

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        await db.Database.MigrateAsync();
    }
}