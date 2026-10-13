using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wallet.Domain;
using Wallet.Infrastructure.Messaging;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Transfers;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class RelayTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitmq;
    private WalletDbContext _db = null!;
    private TransferService _service = null!;
    private IConnection _connection = null!;
    private IModel _channel = null!;
    private ILogger<RelayService> _logger = null!;

    public RelayTests()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17")
            .WithDatabase("wallet_test")
            .WithUsername("wallet")
            .WithPassword("wallet")
            .Build();

        _rabbitmq = new RabbitMqBuilder()
            .WithImage("rabbitmq:4-management")
            .WithUsername("guest")
            .WithPassword("guest")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _rabbitmq.StartAsync();

        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        _db = new WalletDbContext(options);
        await _db.Database.MigrateAsync();

        var systemWallet = Wallet.Domain.Wallet.CreateSystem("GHS");
        _db.Wallets.Add(systemWallet);
        await _db.SaveChangesAsync();

        _service = new TransferService(new DbContextFactoryWrapper(_postgres.GetConnectionString()));

        var factory = new ConnectionFactory { Uri = new Uri(_rabbitmq.GetConnectionString()) };
        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        _logger = loggerFactory.CreateLogger<RelayService>();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbitmq.DisposeAsync();
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
    public async Task Relay_PublishesUnpublishedEvent()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        var relay = new RelayService(_logger, new ScopeFactoryWrapper(), _connection,
            Options.Create(new RelayOptions { PollIntervalMs = 100, BatchSize = 100 }));

        relay.Initialize();

        var published = await WaitForConditionAsync(async () =>
        {
            var evt = await _db.OutboxEvents.AsNoTracking().FirstOrDefaultAsync(e => e.TransferId == transfer.Id);
            return evt?.PublishedAt != null;
        }, TimeSpan.FromSeconds(10));

        published.Should().BeTrue("the relay should publish and mark the event");
    }

    [Fact]
    public async Task Relay_SkipsAlreadyPublishedEvent()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var transfer = await _service.ExecuteAsync(
            TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        await _db.OutboxEvents.Where(e => e.TransferId == transfer.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PublishedAt, DateTimeOffset.UtcNow));
        await _db.SaveChangesAsync();

        var relay = new RelayService(_logger, new ScopeFactoryWrapper(), _connection,
            Options.Create(new RelayOptions { PollIntervalMs = 100, BatchSize = 100 }));

        relay.Initialize();

        await Task.Delay(500);

        var evt = await _db.OutboxEvents.AsNoTracking().FirstOrDefaultAsync(e => e.TransferId == transfer.Id);
        evt.Should().NotBeNull();
        evt!.PublishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Relay_PollsInOrder()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 10);
        await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 10);
        await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 10);

        var relay = new RelayService(_logger, new ScopeFactoryWrapper(), _connection,
            Options.Create(new RelayOptions { PollIntervalMs = 100, BatchSize = 100 }));

        relay.Initialize();

        var allPublished = await WaitForConditionAsync(async () =>
        {
            var count = await _db.OutboxEvents.CountAsync(e => e.PublishedAt != null);
            return count >= 3;
        }, TimeSpan.FromSeconds(10));

        allPublished.Should().BeTrue("all events should be published in order");
    }

    [Fact]
    public async Task TopologySetup_CreatesExpectedQueues()
    {
        RabbitMqTopology.DeclareTopology(_channel);

        _channel.QueueDeclarePassive(RabbitMqTopology.MainQueueName);
        _channel.QueueDeclarePassive(RabbitMqTopology.DlqName);
    }

    private static async Task<bool> WaitForConditionAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < timeout)
        {
            if (await condition())
                return true;
            await Task.Delay(100);
        }
        return false;
    }
}

public class ScopeFactoryWrapper : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new SimpleScope();
}

public class SimpleScope : IServiceScope
{
    public IServiceProvider ServiceProvider { get; } = new SimpleProvider();
    public void Dispose() { }
}

public class SimpleProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}