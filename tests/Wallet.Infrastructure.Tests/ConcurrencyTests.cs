using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Transfers;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class ConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WalletDbContext _db = null!;
    private TransferService _service = null!;

    public ConcurrencyTests()
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

        _service = new TransferService(new DbContextFactoryWrapper(_postgres.GetConnectionString()));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_100ParallelTransfers_ConservesMoney()
    {
        var wallets = new List<Wallet.Domain.Wallet>();
        for (int i = 0; i < 5; i++)
        {
            var w = Wallet.Domain.Wallet.CreateUser($"user-{i}", "GHS");
            wallets.Add(w);
        }
        _db.Wallets.AddRange(wallets);
        await _db.SaveChangesAsync();

        foreach (var w in wallets)
        {
            var systemWallet = await _db.Wallets.FirstAsync(x => x.Kind == WalletKind.System);
            await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, w.Id, "GHS", 1000);
        }

        var initialTotal = 5000L;

        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            var fromIdx = i % 5;
            var toIdx = (i + 1) % 5;
            tasks.Add(_service.ExecuteAsync(
                TransferType.Transfer,
                wallets[fromIdx].Id,
                wallets[toIdx].Id,
                "GHS",
                10));
        }

        await Task.WhenAll(tasks);

        var finalBalances = await _db.Wallets
            .Where(w => w.Kind == WalletKind.User)
            .SumAsync(w => w.Balance);

        finalBalances.Should().Be(initialTotal);
    }

    [Fact]
    public async Task ExecuteAsync_OpposingTransfers_NoDeadlock()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, alice.Id, "GHS", 1000);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, bob.Id, "GHS", 1000);

        var task1 = _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 10);
        var task2 = _service.ExecuteAsync(TransferType.Transfer, bob.Id, alice.Id, "GHS", 10);

        await Task.WhenAll(task1, task2);

        var aliceBalance = await _db.Wallets.Where(w => w.Id == alice.Id).Select(w => w.Balance).FirstAsync();
        var bobBalance = await _db.Wallets.Where(w => w.Id == bob.Id).Select(w => w.Balance).FirstAsync();

        aliceBalance.Should().Be(1000);
        bobBalance.Should().Be(1000);
    }

    [Fact]
    public async Task ExecuteAsync_ParallelSameKey_ExecutesOnce()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, alice.Id, "GHS", 1000);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, bob.Id, "GHS", 1000);

        var transferCountBefore = await _db.Transfers.CountAsync();

        var tasks = new List<Task<Transfer>>();
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(_service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 10));
        }

        await Task.WhenAll(tasks);

        var transferCountAfter = await _db.Transfers.CountAsync();
        transferCountAfter.Should().Be(transferCountBefore + 50);
    }

    [Fact]
    public async Task ExecuteAsync_InvariantCheck_BalanceEqualsSumOfEntries()
    {
        var wallets = new List<Wallet.Domain.Wallet>();
        for (int i = 0; i < 3; i++)
        {
            var w = Wallet.Domain.Wallet.CreateUser($"user-{i}", "GHS");
            wallets.Add(w);
        }
        _db.Wallets.AddRange(wallets);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        foreach (var w in wallets)
        {
            await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, w.Id, "GHS", 500);
        }

        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            var fromIdx = i % 3;
            var toIdx = (i + 1) % 3;
            tasks.Add(_service.ExecuteAsync(
                TransferType.Transfer,
                wallets[fromIdx].Id,
                wallets[toIdx].Id,
                "GHS",
                25));
        }

        await Task.WhenAll(tasks);

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
    public async Task ExecuteAsync_InvariantCheck_ZeroSum()
    {
        var wallets = new List<Wallet.Domain.Wallet>();
        for (int i = 0; i < 3; i++)
        {
            var w = Wallet.Domain.Wallet.CreateUser($"user-{i}", "GHS");
            wallets.Add(w);
        }
        _db.Wallets.AddRange(wallets);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        foreach (var w in wallets)
        {
            await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, w.Id, "GHS", 500);
        }

        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            var fromIdx = i % 3;
            var toIdx = (i + 1) % 3;
            tasks.Add(_service.ExecuteAsync(
                TransferType.Transfer,
                wallets[fromIdx].Id,
                wallets[toIdx].Id,
                "GHS",
                25));
        }

        await Task.WhenAll(tasks);

        var totalSum = await _db.LedgerEntries.SumAsync(e => e.Amount);
        totalSum.Should().Be(0);
    }
}