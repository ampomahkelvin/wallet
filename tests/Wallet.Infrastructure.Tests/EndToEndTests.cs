using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

public class EndToEndTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitmq;
    private WalletDbContext _db = null!;
    private TransferService _service = null!;
    private IConnection _connection = null!;
    private IModel _channel = null!;

    public EndToEndTests()
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
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbitmq.DisposeAsync();
    }

    [Fact]
    public async Task EndToEnd_1000ConcurrentTransfers_AllConverge()
    {
        var wallets = new List<Wallet.Domain.Wallet>();
        for (int i = 0; i < 10; i++)
        {
            var w = Wallet.Domain.Wallet.CreateUser($"e2e-user-{i}", "GHS");
            wallets.Add(w);
        }
        _db.Wallets.AddRange(wallets);
        await _db.SaveChangesAsync();

        var systemWallet = await _db.Wallets.FirstAsync(w => w.Kind == WalletKind.System);
        foreach (var w in wallets)
        {
            await _service.ExecuteAsync(TransferType.Deposit, systemWallet.Id, w.Id, "GHS", 10000);
        }

        var transferCountBefore = await _db.Transfers.CountAsync();

        var tasks = new List<Task>();
        for (int i = 0; i < 1000; i++)
        {
            var fromIdx = i % 10;
            var toIdx = (i + 1) % 10;
            tasks.Add(_service.ExecuteAsync(
                TransferType.Transfer,
                wallets[fromIdx].Id,
                wallets[toIdx].Id,
                "GHS",
                10));
        }

        await Task.WhenAll(tasks);

        var transferCountAfter = await _db.Transfers.CountAsync();
        transferCountAfter.Should().Be(transferCountBefore + 1000);

        var totalBalance = await _db.Wallets
            .Where(w => w.Kind == WalletKind.User)
            .SumAsync(w => w.Balance);
        totalBalance.Should().Be(100000);

        var zeroSum = await _db.LedgerEntries.SumAsync(e => e.Amount);
        zeroSum.Should().Be(0);

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

        var outboxCount = await _db.OutboxEvents.CountAsync();
        outboxCount.Should().BeGreaterThanOrEqualTo(1000);

        var relay = new RelayService(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<RelayService>(),
            new ScopeFactoryWrapper(_postgres.GetConnectionString()),
            _connection,
            Microsoft.Extensions.Options.Options.Create(new RelayOptions { PollIntervalMs = 100, BatchSize = 500 }));
        relay.Initialize();

        var allPublished = await WaitForConditionAsync(async () =>
        {
            await relay.PollAndPublishAsync(CancellationToken.None);
            var unpublished = await _db.OutboxEvents.CountAsync(e => e.PublishedAt == null);
            return unpublished == 0;
        }, TimeSpan.FromSeconds(30));

        allPublished.Should().BeTrue("all outbox events should be published");

        var publishedCount = await _db.OutboxEvents.CountAsync(e => e.PublishedAt != null);
        publishedCount.Should().Be(outboxCount);
    }

    private static async Task<bool> WaitForConditionAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < timeout)
        {
            if (await condition())
                return true;
            await Task.Delay(200);
        }
        return false;
    }
}

