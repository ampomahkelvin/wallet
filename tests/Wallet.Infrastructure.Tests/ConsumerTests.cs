using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

public class ConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitmq;
    private WalletDbContext _db = null!;
    private TransferService _service = null!;
    private RabbitMqConnectionProvider _connectionProvider = null!;
    private IModel _channel = null!;
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

        _connectionProvider = new RabbitMqConnectionProvider(
            _rabbitmq.GetConnectionString(),
            NullLogger<RabbitMqConnectionProvider>.Instance);
        var connection = await _connectionProvider.GetConnectionAsync();
        _channel = connection.CreateModel();
        RabbitMqTopology.DeclareTopology(_channel, TimeSpan.FromMilliseconds(500));
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
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, alice.Id, "GHS", 1000, alice.OwnerId!);
        await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, bob.Id, "GHS", 1000, bob.OwnerId!);

        return (alice, bob);
    }

    private EventDispatcher CreateDispatcher(string eventType, Func<string, Task> handler)
    {
        var dispatcher = new EventDispatcher(NullLogger<EventDispatcher>.Instance);
        dispatcher.Register(eventType, handler);
        return dispatcher;
    }

    private ConsumerService CreateConsumer(EventDispatcher dispatcher, int maxRetryCount = 3)
        => new(
            NullLogger<ConsumerService>.Instance,
            new TestServiceProvider(new DbContextFactoryWrapper(_postgres.GetConnectionString())),
            _connectionProvider,
            dispatcher,
            Options.Create(new ConsumerOptions { MaxRetryCount = maxRetryCount, PrefetchCount = 10 }),
            TimeSpan.FromMilliseconds(500));

    private void PublishEvent(long eventId, string eventType, string payload, int retryCount = 0)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var props = _channel.CreateBasicProperties();
        props.Persistent = true;
        props.MessageId = eventId.ToString();
        props.Type = eventType;
        if (retryCount > 0)
            props.Headers = new Dictionary<string, object> { [RabbitMqTopology.RetryCountHeader] = retryCount };

        _channel.BasicPublish(RabbitMqTopology.ExchangeName, eventType, mandatory: true, props, body);
    }

    [Fact]
    public async Task ConsumerService_NewEvent_RecordsInProcessedEvents()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var transfer = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
        var evt = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == transfer.Id);

        var consumer = CreateConsumer(CreateDispatcher("transfer.completed", _ => Task.CompletedTask));
        await consumer.StartAsync(CancellationToken.None);

        PublishEvent(evt.Id, "transfer.completed", evt.Payload);

        var processed = await WaitForConditionAsync(async () =>
            await _db.ConsumerProcessedEvents.AnyAsync(e => e.EventId == evt.Id && e.ConsumerName == "wallet-consumer"),
            TimeSpan.FromSeconds(10));
        processed.Should().BeTrue();

        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConsumerService_DuplicateEvent_ProcessesOnce()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var transfer = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
        var evt = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == transfer.Id);

        var consumer = CreateConsumer(CreateDispatcher("transfer.completed", async payload =>
        {
            _processedEvents.Add(payload);
            await Task.CompletedTask;
        }));
        await consumer.StartAsync(CancellationToken.None);

        PublishEvent(evt.Id, "transfer.completed", evt.Payload);
        PublishEvent(evt.Id, "transfer.completed", evt.Payload);

        var processed = await WaitForConditionAsync(() => Task.FromResult(_processedEvents.Count == 1), TimeSpan.FromSeconds(10));
        processed.Should().BeTrue();

        await Task.Delay(500);
        _processedEvents.Should().HaveCount(1);
        (await _db.ConsumerProcessedEvents.CountAsync(e => e.EventId == evt.Id)).Should().Be(1);

        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConsumerService_UnknownEventType_AcksWithoutProcessing()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var transfer = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
        var evt = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == transfer.Id);

        var consumer = CreateConsumer(CreateDispatcher("transfer.completed", async payload =>
        {
            _processedEvents.Add(payload);
            await Task.CompletedTask;
        }));
        await consumer.StartAsync(CancellationToken.None);

        PublishEvent(evt.Id, "unknown.event", evt.Payload);
        await Task.Delay(500);

        _processedEvents.Should().BeEmpty();

        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConsumerService_MultipleEvents_AllProcessed()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();

        var t1 = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 10, alice.OwnerId!);
        var t2 = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 20, alice.OwnerId!);
        var t3 = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 30, alice.OwnerId!);

        var e1 = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == t1.Id);
        var e2 = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == t2.Id);
        var e3 = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == t3.Id);

        var consumer = CreateConsumer(CreateDispatcher("transfer.completed", async payload =>
        {
            _processedEvents.Add(payload);
            await Task.CompletedTask;
        }));
        await consumer.StartAsync(CancellationToken.None);

        PublishEvent(e1.Id, "transfer.completed", e1.Payload);
        PublishEvent(e2.Id, "transfer.completed", e2.Payload);
        PublishEvent(e3.Id, "transfer.completed", e3.Payload);

        var processed = await WaitForConditionAsync(() => Task.FromResult(_processedEvents.Count == 3), TimeSpan.FromSeconds(10));
        processed.Should().BeTrue();

        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConsumerService_TransientFailure_RetriesAndProcessesOnce()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var transfer = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
        var evt = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == transfer.Id);

        var attempts = 0;
        var consumer = CreateConsumer(CreateDispatcher("transfer.completed", _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("transient failure");

            return Task.CompletedTask;
        }));
        await consumer.StartAsync(CancellationToken.None);

        PublishEvent(evt.Id, "transfer.completed", evt.Payload);

        var processed = await WaitForConditionAsync(async () =>
            await _db.ConsumerProcessedEvents.AnyAsync(e => e.EventId == evt.Id && e.ConsumerName == "wallet-consumer"),
            TimeSpan.FromSeconds(15));
        processed.Should().BeTrue("the retried event should eventually be processed");

        attempts.Should().Be(2);
        (await _db.ConsumerProcessedEvents.CountAsync(e => e.EventId == evt.Id)).Should().Be(1);

        await consumer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConsumerService_PoisonMessage_GoesToDlq()
    {
        var (alice, bob) = await CreateFundedWalletsAsync();
        var transfer = await _service.ExecuteAsync(TransferType.Transfer, alice.Id, bob.Id, "GHS", 100, alice.OwnerId!);
        var evt = await _db.OutboxEvents.AsNoTracking().FirstAsync(e => e.TransferId == transfer.Id);

        var consumer = CreateConsumer(CreateDispatcher("transfer.completed", _ => throw new InvalidOperationException("poison")));
        await consumer.StartAsync(CancellationToken.None);

        PublishEvent(evt.Id, "transfer.completed", evt.Payload, retryCount: 3);

        var deadLettered = await WaitForConditionAsync(
            () => Task.FromResult(_channel.MessageCount(RabbitMqTopology.DlqName) >= 1),
            TimeSpan.FromSeconds(15));
        deadLettered.Should().BeTrue("a poison message should reach the DLQ");

        (await _db.ConsumerProcessedEvents.CountAsync(e => e.EventId == evt.Id)).Should().Be(0);

        await consumer.StopAsync(CancellationToken.None);
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

public class TestServiceProvider(IDbContextFactory<WalletDbContext> factory) : IServiceProvider, IServiceScopeFactory
{
    public IServiceScope CreateScope() => new TestScope(factory);

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceScopeFactory))
            return this;
        if (serviceType == typeof(WalletDbContext))
            return factory.CreateDbContext();
        return null;
    }
}

public class TestScope(IDbContextFactory<WalletDbContext> factory) : IServiceScope
{
    public IServiceProvider ServiceProvider { get; } = new TestServiceProvider(factory);
    public void Dispose() { }
}