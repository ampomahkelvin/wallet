using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Transfers;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class FailureInjectionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WalletDbContext _db = null!;
    private TransferService _service = null!;

    public FailureInjectionTests()
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
    public async Task ExecuteAsync_WhenSaveChangesFails_RollsBack()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, alice.Id, "GHS", 1000);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, bob.Id, "GHS", 1000);

        var transferCountBefore = await _db.Transfers.CountAsync();

        var invalidTransfer = Transfer.Create(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);
        _db.Transfers.Add(invalidTransfer);

        Assert.Throws<InvalidOperationException>(() => _db.Wallets.Remove(alice));

        _db.ChangeTracker.Clear();
        await _db.SaveChangesAsync();

        var aliceAfter = await _db.Wallets.FindAsync(alice.Id);
        aliceAfter.Should().NotBeNull();

        var transferCountAfter = await _db.Transfers.CountAsync();
        transferCountAfter.Should().Be(transferCountBefore);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNegativeBalance_ConstraintRejects()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<PostgresException>(async () =>
        {
            await using var db = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>()
                .UseNpgsql(_postgres.GetConnectionString()).Options);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE wallets SET ""Balance"" = -1 WHERE ""Id"" = {alice.Id}");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenDuplicateReversal_ConstraintRejects()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, alice.Id, "GHS", 1000);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, bob.Id, "GHS", 1000);

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);
        await _service.ReverseAsync(transfer.Id);

        await Assert.ThrowsAnyAsync<PostgresException>(async () =>
        {
            await using var db = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>()
                .UseNpgsql(_postgres.GetConnectionString()).Options);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO transfers (""Id"", ""Type"", ""SourceWalletId"", ""DestinationWalletId"", ""Currency"", ""Amount"", ""ReversesTransferId"", ""CreatedAt"")
                VALUES ({Guid.NewGuid()}, 4, {bob.Id}, {alice.Id}, 'GHS', 100, {transfer.Id}, now())");
        });
    }
}