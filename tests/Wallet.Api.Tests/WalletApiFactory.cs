using System.Collections.Concurrent;
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
using Wallet.Infrastructure.Accounts;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.RateLimiting;

namespace Wallet.Api.Tests;

public class WalletApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public TestApiKeyProvider ApiKeys { get; } = new();
    public RecordingRateLimiter RateLimiter { get; } = new();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .WithDatabase("wallet_test")
        .WithUsername("wallet")
        .WithPassword("wallet")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IRateLimiter>();
            services.AddSingleton<IRateLimiter>(RateLimiter);
            services.RemoveAll<IApiKeyProvider>();
            services.AddSingleton<IApiKeyProvider>(ApiKeys);

            services.RemoveAll<IDbContextFactory<WalletDbContext>>();
            services.AddSingleton<IDbContextFactory<WalletDbContext>>(
                _ => new TestDbContextFactory(_postgres.GetConnectionString()));
            services.RemoveAll<WalletDbContext>();
            services.AddScoped<WalletDbContext>(
                sp => sp.GetRequiredService<IDbContextFactory<WalletDbContext>>().CreateDbContext());
        });
    }

    public WalletDbContext CreateDbContext()
        => new TestDbContextFactory(_postgres.GetConnectionString()).CreateDbContext();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var systemWallet = Wallet.Domain.Wallet.CreateSystem("GHS");
        db.Wallets.Add(systemWallet);
        await db.SaveChangesAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public class TestDbContextFactory(string connectionString) : IDbContextFactory<WalletDbContext>
{
    public WalletDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new WalletDbContext(options);
    }
}

public class RecordingRateLimiter : IRateLimiter
{
    private readonly ConcurrentQueue<(string ClientId, string Route)> _calls = new();

    public IReadOnlyCollection<(string ClientId, string Route)> Calls => _calls.ToArray();

    public void Clear() => _calls.Clear();

    public Task<bool> IsAllowedAsync(string clientId, string route, CancellationToken ct = default)
    {
        _calls.Enqueue((clientId, route));
        return Task.FromResult(true);
    }
}

public class TestApiKeyProvider : IApiKeyProvider
{
    private readonly ConcurrentDictionary<string, string> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _adminKeys = new(StringComparer.Ordinal);

    public string CreateKey(string clientId, bool admin = false)
    {
        var key = $"test-key-{Guid.NewGuid():N}";
        _keys[key] = clientId;
        if (admin)
            _adminKeys[key] = 0;
        return key;
    }

    public string CreateAdminKey(string clientId = "admin") => CreateKey(clientId, admin: true);

    public bool TryGetClientId(string apiKey, out string clientId)
    {
        if (_keys.TryGetValue(apiKey, out var found))
        {
            clientId = found;
            return true;
        }

        clientId = null!;
        return false;
    }

    public bool IsAdmin(string apiKey) => _adminKeys.ContainsKey(apiKey);
}