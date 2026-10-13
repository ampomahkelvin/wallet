using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Messaging;

public class RelayService : BackgroundService
{
    private readonly ILogger<RelayService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnection _connection;
    private readonly RelayOptions _options;
    private IModel _channel = null!;

    public RelayService(
        ILogger<RelayService> logger,
        IServiceScopeFactory scopeFactory,
        IConnection connection,
        IOptions<RelayOptions> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _connection = connection;
        _options = options.Value;
    }

    public void Initialize()
    {
        _channel = _connection.CreateModel();
        _channel.ConfirmSelect();
        RabbitMqTopology.DeclareTopology(_channel);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Initialize();
        _logger.LogInformation("Relay started. Polling every {PollIntervalMs}ms", _options.PollIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAndPublishAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during relay poll");
            }

            await Task.Delay(_options.PollIntervalMs, stoppingToken);
        }
    }

    private async Task PollAndPublishAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var events = await db.OutboxEvents
            .FromSqlInterpolated($@"
                SELECT ""Id"", ""TransferId"", ""Type"", ""Payload"", ""CreatedAt"", ""PublishedAt""
                FROM outbox_events
                WHERE ""PublishedAt"" IS NULL
                ORDER BY ""Id""
                LIMIT {_options.BatchSize}
                FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return;
        }

        foreach (var evt in events)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(evt.Payload);

                var props = _channel.CreateBasicProperties();
                props.Persistent = true;
                props.MessageId = evt.Id.ToString();
                props.Type = evt.Type;

                _channel.BasicPublish(
                    RabbitMqTopology.ExchangeName,
                    evt.Type,
                    mandatory: true,
                    basicProperties: props,
                    body: body);

                _channel.WaitForConfirms(TimeSpan.FromSeconds(5));

                evt.MarkPublished();
                _logger.LogInformation("Published outbox event {EventId} ({EventType})", evt.Id, evt.Type);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish outbox event {EventId}", evt.Id);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}