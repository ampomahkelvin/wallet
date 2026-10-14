using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wallet.Domain;
using Wallet.Infrastructure.Messaging;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Transfers;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class ConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitmq;
    private WalletDbContext _db = null!;
    private TransferService _service = null!;
    private IConnection _connection = null!;
    private IModel _channel = null!;
    private EventDispatcher _dispatcher = null!;
    private readonly List<string> _processedEvents = [];

    public ConsumerTests()
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
        RabbitMqTopology.DeclareTopology(_channel);

        _dispatcher = new EventDispatcher(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<EventDispatcher>());
        _dispatcher.Register("transfer.completed", async payload =>
        {
            _processedEvents.Add(payload);
            await Task.CompletedTask;
        });
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

    private void PublishEvent(long eventId, string eventType, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var props = _channel.CreateBasicProperties();
        props.Persistent = true;
        props.MessageId = eventId.ToString();
        props.Type = eventType;

        _channel.BasicPublish(RabbitMqTopology.ExchangeName, eventType, mandatory: true, props, body);
    }

    [Fact]
    public async Task Consumer_NewEvent_RecordsInProcessedEvents()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var transfer = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        var evt = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == transfer.Id);
        var payload = evt.Payload;

        await ConsumerMessageAsync(evt.Id.ToString(), "transfer.completed", payload);

        var processed = await _db.ConsumerProcessedEvents
            .FirstOrDefaultAsync(e => e.EventId == evt.Id && e.ConsumerName == "wallet-consumer");

        processed.Should().NotBeNull();
        processed!.EventId.Should().Be(evt.Id);
        processed.ConsumerName.Should().Be("wallet-consumer");
    }

    [Fact]
    public async Task Consumer_DuplicateEvent_ProcessesOnce()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var transfer = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        var evt = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == transfer.Id);

        await ConsumerMessageAsync(evt.Id.ToString(), "transfer.completed", evt.Payload);
        await ConsumerMessageAsync(evt.Id.ToString(), "transfer.completed", evt.Payload);

        _processedEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task Consumer_UnknownEventType_AcksWithoutProcessing()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var transfer = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100);

        var evt = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == transfer.Id);

        await ConsumerMessageAsync(evt.Id.ToString(), "unknown.event", evt.Payload);

        _processedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Consumer_MultipleEvents_AllProcessed()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var t1 = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 10);
        var t2 = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 20);
        var t3 = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 30);

        var e1 = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == t1.Id);
        var e2 = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == t2.Id);
        var e3 = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == t3.Id);

        await ConsumerMessageAsync(e1.Id.ToString(), "transfer.completed", e1.Payload);
        await ConsumerMessageAsync(e2.Id.ToString(), "transfer.completed", e2.Payload);
        await ConsumerMessageAsync(e3.Id.ToString(), "transfer.completed", e3.Payload);

        _processedEvents.Should().HaveCount(3);
    }

    private async Task ConsumerMessageAsync(string messageId, string eventType, string payload)
    {
        var options = new DbContextOptionsBuilder<WalletDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        await using var db = new WalletDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync();

        var existing = await db.ConsumerProcessedEvents
            .AnyAsync(e => e.EventId == long.Parse(messageId) && e.ConsumerName == "wallet-consumer");

        if (existing)
        {
            await tx.CommitAsync();
            return;
        }

        await _dispatcher.DispatchAsync(eventType, payload);

        db.ConsumerProcessedEvents.Add(ConsumerProcessedEvent.Create(long.Parse(messageId), "wallet-consumer"));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}