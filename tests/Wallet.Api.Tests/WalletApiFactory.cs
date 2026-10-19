using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Wallet.Api;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.RateLimiting;

namespace Wallet.Api.Tests;

public class WalletApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .WithDatabase("wallet_test")
        .WithUsername("wallet")
        .WithPassword("wallet")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IRateLimiter>();
            services.AddSingleton<IRateLimiter, AllowAllRateLimiter>();
        });
    }

    public WalletDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        return new WalletDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__RabbitMQ", "amqp://localhost:5672/");
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", "localhost:6379");
        Environment.SetEnvironmentVariable("Relay__PollIntervalMs", "60000");

        using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var systemWallet = Wallet.Domain.Wallet.CreateSystem("GHS");
        db.Wallets.Add(systemWallet);
        await db.SaveChangesAsync();
    }

    public new async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__RabbitMQ", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", null);
        Environment.SetEnvironmentVariable("Relay__PollIntervalMs", null);
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public class AllowAllRateLimiter : IRateLimiter
{
    public Task<bool> IsAllowedAsync(string clientId, string route, CancellationToken ct = default)
        => Task.FromResult(true);
}