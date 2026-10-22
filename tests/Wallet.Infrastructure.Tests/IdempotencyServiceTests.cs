using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Wallet.Domain;
using Wallet.Infrastructure.Idempotency;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Transfers;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class IdempotencyServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WalletDbContext _db = null!;
    private TransferService _transfers = null!;
    private IdempotencyService _idempotency = null!;

    public IdempotencyServiceTests()
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

        var factory = new DbContextFactoryWrapper(_postgres.GetConnectionString());
        _transfers = new TransferService(factory);
        _idempotency = new IdempotencyService(factory, NullLogger<IdempotencyService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_FaultAfterWrite_RollsBackTransferAndKey()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var hash = new byte[32];

        await Assert.ThrowsAsync<InvalidOperationException>(() => _idempotency.HandleAsync<TestResponse>(
            "client-1", "key-1", hash,
            async db =>
            {
                await _transfers.ExecuteInTransactionAsync(
                    db, TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
                throw new InvalidOperationException("injected fault after write");
            }));

        (await _db.Transfers.CountAsync()).Should().Be(2);
        (await _db.IdempotencyKeys.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_Replay_ReturnsCachedResponseWithoutNewRows()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var hash = new byte[32];

        var (first, _, _) = await _idempotency.HandleAsync(
            "client-1", "key-1", hash,
            async db =>
            {
                var t = await _transfers.ExecuteInTransactionAsync(
                    db, TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
                return (new TestResponse(t.Id), (short)201);
            });

        var (second, status, replayed) = await _idempotency.HandleAsync(
            "client-1", "key-1", hash,
            async db =>
            {
                var t = await _transfers.ExecuteInTransactionAsync(
                    db, TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
                return (new TestResponse(t.Id), (short)201);
            });

        replayed.Should().BeTrue();
        status.Should().Be(201);
        second.Should().Be(first);

        (await _db.Transfers.CountAsync()).Should().Be(3);
        (await _db.OutboxEvents.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task HandleAsync_SameKeyDifferentPayload_Throws()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var hash1 = new byte[32];
        var hash2 = new byte[32];
        hash2[0] = 1;

        await _idempotency.HandleAsync(
            "client-1", "key-1", hash1,
            async db =>
            {
                var t = await _transfers.ExecuteInTransactionAsync(
                    db, TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
                return (new TestResponse(t.Id), (short)201);
            });

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() => _idempotency.HandleAsync(
            "client-1", "key-1", hash2,
            async db =>
            {
                var t = await _transfers.ExecuteInTransactionAsync(
                    db, TransferType.Transfer, alice.Id, bob.Id, "GHS", 200, alice.OwnerId!);
                return (new TestResponse(t.Id), (short)201);
            }));
    }

    [Fact]
    public async Task HandleAsync_50ParallelSameKey_ExecutesOnce()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var hash = new byte[32];

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => _idempotency.HandleAsync(
            "client-1", "key-1", hash,
            async db =>
            {
                var t = await _transfers.ExecuteInTransactionAsync(
                    db, TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
                return (new TestResponse(t.Id), (short)201);
            })));

        results.Select(r => r.Body.Id).Distinct().Should().HaveCount(1);
        (await _db.Transfers.CountAsync()).Should().Be(3);
    }

    private async Task<(Wallet.Domain.Wallet alice, Wallet.Domain.Wallet bob)> CreateFundedWalletsAsync()
    {
        var alice = Wallet.Domain.Wallet.CreateUser($"alice-{Guid.NewGuid():N}", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser($"bob-{Guid.NewGuid():N}", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        await _transfers.ExecuteAsync(TransferType.Deposit, systemWallet.Id, alice.Id, "GHS", 10000, "test-client");
        await _transfers.ExecuteAsync(TransferType.Deposit, systemWallet.Id, bob.Id, "GHS", 10000, "test-client");

        return (alice, bob);
    }

    private sealed record TestResponse(Guid Id);
}