using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Transfers;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class TransferServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WalletDbContext _db = null!;
    private TransferService _service = null!;

    public TransferServiceTests()
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

        var serviceOptions = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        var serviceDb = new WalletDbContext(serviceOptions);
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
    public async Task ExecuteAsync_WithValidTransfer_CreatesBalancedEntries()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        transfer.Entries.Should().HaveCount(2);
        transfer.Entries.Sum(e => e.Amount).Should().Be(0);

        var aliceEntry = transfer.Entries.First(e => e.WalletId == alice.Id);
        var bobEntry = transfer.Entries.First(e => e.WalletId == bob.Id);
        aliceEntry.Amount.Should().Be(-100);
        bobEntry.Amount.Should().Be(100);
    }

    [Fact]
    public async Task ExecuteAsync_WithInsufficientFunds_Throws()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "GHS");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100));
    }

    [Fact]
    public async Task ExecuteAsync_WithCurrencyMismatch_Throws()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        var bob = Wallet.Domain.Wallet.CreateUser("bob", "USD");
        _db.Wallets.AddRange(alice, bob);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100));
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownWallet_Throws()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        _db.Wallets.Add(alice);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.ExecuteAsync(TransferType.Transfer, alice.Id, Guid.NewGuid(), "GHS", 100));
    }

    [Fact]
    public async Task ExecuteAsync_WithNonPositiveAmount_Throws()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 0));
    }

    [Fact]
    public async Task ExecuteAsync_WithSameSourceAndDest_Throws()
    {
        var alice = Wallet.Domain.Wallet.CreateUser("alice", "GHS");
        _db.Wallets.Add(alice);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.ExecuteAsync(TransferType.Transfer, alice.Id, alice.Id, "GHS", 100));
    }

    [Fact]
    public async Task ReverseAsync_WithValidTransfer_CreatesReversal()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        var reversal = await _service.ReverseAsync(transfer.Id);

        reversal.Type.Should().Be(TransferType.Reversal);
        reversal.ReversesTransferId.Should().Be(transfer.Id);
        reversal.Amount.Should().Be(100);
    }

    [Fact]
    public async Task ReverseAsync_WhenAlreadyReversed_Throws()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);
        await _service.ReverseAsync(transfer.Id);

        await Assert.ThrowsAsync<ConflictException>(() => _service.ReverseAsync(transfer.Id));
    }

    [Fact]
    public async Task ReverseAsync_WhenReversal_Throws()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);
        var reversal = await _service.ReverseAsync(transfer.Id);

        await Assert.ThrowsAsync<ValidationException>(() => _service.ReverseAsync(reversal.Id));
    }

    [Fact]
    public async Task GetAsync_WithExistingTransfer_ReturnsTransfer()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        var fetched = await _service.GetAsync(transfer.Id);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(transfer.Id);
    }

    [Fact]
    public async Task GetAsync_WithUnknownTransfer_ReturnsNull()
    {
        var result = await _service.GetAsync(Guid.NewGuid());
        result.Should().BeNull();
    }
}