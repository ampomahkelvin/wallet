using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class MaintenanceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WalletDbContext _db = null!;

    public MaintenanceTests()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17")
            .WithDatabase("wallet_test")
            .WithUsername("wallet")
            .WithPassword("wallet")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        _db = new WalletDbContext(options);
        await _db.Database.MigrateAsync();

        var systemWallet = Wallet.Domain.Wallet.CreateSystem("GHS");
        _db.Wallets.Add(systemWallet);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Maintenance_DeletesExpiredKeys_KeepsLiveKeys()
    {
        var expiredKey = IdempotencyKey.Claim("client-1", "expired-key", new byte[32], TimeSpan.FromHours(-1));
        var liveKey = IdempotencyKey.Claim("client-2", "live-key", new byte[32], TimeSpan.FromHours(24));

        _db.IdempotencyKeys.AddRange(expiredKey, liveKey);
        await _db.SaveChangesAsync();

        var before = await _db.IdempotencyKeys.CountAsync();
        before.Should().Be(2);

        var now = DateTimeOffset.UtcNow;
        var deleted = await _db.IdempotencyKeys
            .Where(k => k.ExpiresAt < now)
            .ExecuteDeleteAsync();

        deleted.Should().Be(1);

        var remaining = await _db.IdempotencyKeys.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].Key.Should().Be("live-key");
    }

    [Fact]
    public async Task Maintenance_DeletesOldPublishedEvents_KeepsRecent()
    {
        var wallet = Wallet.Domain.Wallet.CreateUser("user-1", "GHS");
        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync();

        var wallet2 = Wallet.Domain.Wallet.CreateUser("user-2", "GHS");
        _db.Wallets.Add(wallet2);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        var service = new Wallet.Infrastructure.Transfers.TransferService(
            new DbContextFactoryWrapper(_postgres.GetConnectionString()));

        var transfer = await service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, wallet.Id, "GHS", 1000);
        var secondTransfer = await service.ExecuteAsync(TransferType.Transfer, wallet.Id, wallet2.Id, "GHS", 100);

        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO outbox_events (""TransferId"", ""Type"", ""Payload"", ""CreatedAt"", ""PublishedAt"")
            VALUES ({transfer.Id}, 'transfer.reversed', '{{}}', now(), now() - interval '10 days')");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO outbox_events (""TransferId"", ""Type"", ""Payload"", ""CreatedAt"", ""PublishedAt"")
            VALUES ({secondTransfer.Id}, 'deposit.completed', '{{}}', now(), now())");

        var now = DateTimeOffset.UtcNow;
        var deleted = await _db.OutboxEvents
            .Where(e => e.PublishedAt < now.AddDays(-7))
            .ExecuteDeleteAsync();

        deleted.Should().Be(1);

        var remaining = await _db.OutboxEvents.ToListAsync();
        remaining.Should().HaveCount(3);
        remaining.Should().Contain(e => e.Type == "deposit.completed");
        remaining.Should().Contain(e => e.Type == "transfer.completed");
    }

    [Fact]
    public async Task Maintenance_Reconciliation_BalanceEqualsSumOfEntries()
    {
        var wallet = Wallet.Domain.Wallet.CreateUser("user-1", "GHS");
        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);

        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        var serviceDb = new WalletDbContext(options);
        var service = new Wallet.Infrastructure.Transfers.TransferService(
            new DbContextFactoryWrapper(_postgres.GetConnectionString()));

        await service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, wallet.Id, "GHS", 500);

        var walletsWithBalance = await _db.Wallets
            .Where(w => w.Kind == WalletKind.User)
            .Select(w => new { w.Id, w.Balance })
            .ToListAsync();

        foreach (var w in walletsWithBalance)
        {
            var sum = await _db.LedgerEntries
                .Where(e => e.WalletId == w.Id)
                .SumAsync(e => e.Amount);
            sum.Should().Be(w.Balance);
        }
    }

    [Fact]
    public async Task Maintenance_Reconciliation_ZeroSum()
    {
        var wallet = Wallet.Domain.Wallet.CreateUser("user-1", "GHS");
        _db.Wallets.Add(wallet);
        await _db.SaveChangesAsync();

        var service = new Wallet.Infrastructure.Transfers.TransferService(
            new DbContextFactoryWrapper(_postgres.GetConnectionString()));

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        await service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, wallet.Id, "GHS", 500);

        var totalSum = await _db.LedgerEntries.SumAsync(e => e.Amount);
        totalSum.Should().Be(0);
    }
}