using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Transfers;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class OutboxTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WalletDbContext _db = null!;
    private TransferService _service = null!;

    public OutboxTests()
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

    private async Task<(Wallet.Domain.Wallet alice, Wallet.Domain.Wallet bob)> CreateFundedWalletsAsync()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, alice.Id, "GHS", 1000);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, bob.Id, "GHS", 1000);

        return (alice, bob);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidTransfer_CreatesOutboxEvent()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        var outboxEvent = await _db.OutboxEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TransferId == transfer.Id);

        outboxEvent.Should().NotBeNull();
        outboxEvent!.Type.Should().Be("transfer.completed");
        outboxEvent.PublishedAt.Should().BeNull();

        var payload = JsonSerializer.Deserialize<JsonElement>(outboxEvent.Payload);
        payload.GetProperty("Id").GetGuid().Should().Be(transfer.Id);
        payload.GetProperty("Amount").GetInt64().Should().Be(100);
        payload.GetProperty("Currency").GetString().Should().Be("GHS");
    }

    [Fact]
    public async Task ExecuteAsync_WhenTransferRollsBack_OutboxEventRollsBack()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, alice.Id, "GHS", 1000);

        var transferCountBefore = await _db.Transfers.CountAsync();
        var outboxCountBefore = await _db.OutboxEvents.CountAsync();

        await Assert.ThrowsAnyAsync<PostgresException>(async () =>
        {
            await using var db = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>()
                .UseNpgsql(_postgres.GetConnectionString()).Options);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO transfers (""Id"", ""Type"", ""SourceWalletId"", ""DestinationWalletId"", ""Currency"", ""Amount"", ""CreatedAt"")
                VALUES ({Guid.NewGuid()}, 1, {alice.Id}, {alice.Id}, 'GHS', 100, now())");
        });

        var transferCountAfter = await _db.Transfers.CountAsync();
        var outboxCountAfter = await _db.OutboxEvents.CountAsync();

        transferCountAfter.Should().Be(transferCountBefore);
        outboxCountAfter.Should().Be(outboxCountBefore);
    }

    [Fact]
    public async Task ReverseAsync_WithValidReversal_CreatesOutboxEvent()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);
        var reversal = await _service.ReverseAsync(transfer.Id);

        var outboxEvent = await _db.OutboxEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TransferId == reversal.Id);

        outboxEvent.Should().NotBeNull();
        outboxEvent!.Type.Should().Be("transfer.reversed");
    }

    [Theory]
    [InlineData(TransferType.Transfer, "transfer.completed")]
    [InlineData(TransferType.Deposit, "deposit.completed")]
    [InlineData(TransferType.Withdrawal, "withdrawal.completed")]
    [InlineData(TransferType.Reversal, "transfer.reversed")]
    public async Task ExecuteAsync_EventTypeMapping_MatchesExpectedType(TransferType type, string expectedEventType)
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        Transfer transfer;
        if (type == TransferType.Deposit)
            transfer = await _service.ExecuteAsync(type, alice.Id, bob.Id, "GHS", 100);
        else if (type == TransferType.Withdrawal)
            transfer = await _service.ExecuteAsync(type, alice.Id, bob.Id, "GHS", 100);
        else if (type == TransferType.Reversal)
        {
            var original = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);
            transfer = await _service.ReverseAsync(original.Id);
        }
        else
            transfer = await _service.ExecuteAsync(type, alice.Id, bob.Id, "GHS", 100);

        var outboxEvent = await _db.OutboxEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TransferId == transfer.Id);

        outboxEvent.Should().NotBeNull();
        outboxEvent!.Type.Should().Be(expectedEventType);
    }

    [Fact]
    public async Task ExecuteAsync_OneEventPerTransferAndType()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        var events = await _db.OutboxEvents
            .AsNoTracking()
            .Where(e => e.TransferId == transfer.Id)
            .ToListAsync();

        events.Should().HaveCount(1);
    }
}