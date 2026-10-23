using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Messaging;

public class RelayService : BackgroundService
{
    private readonly ILogger<RelayService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly RelayOptions _options;
    private readonly ConcurrentDictionary<string, byte> _returnedMessages = new(StringComparer.Ordinal);
    private IModel? _channel;

    public RelayService(
        ILogger<RelayService> logger,
        IServiceScopeFactory scopeFactory,
        RabbitMqConnectionProvider connectionProvider,
        IOptions<RelayOptions> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _connectionProvider = connectionProvider;
        _options = options.Value;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var connection = await _connectionProvider.GetConnectionAsync(ct);
        _channel = connection.CreateModel();
        _channel.ConfirmSelect();
        _channel.BasicReturn += OnBasicReturn;
        RabbitMqTopology.DeclareTopology(_channel);
    }

    private void OnBasicReturn(object? sender, BasicReturnEventArgs e)
    {
        if (e.BasicProperties.MessageId is { } messageId)
            _returnedMessages[messageId] = 0;

        _logger.LogWarning(
            "Message returned: reply code {ReplyCode}, exchange {Exchange}, routing key {RoutingKey}",
            e.ReplyCode, e.Exchange, e.RoutingKey);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeAsync(stoppingToken);
        _logger.LogInformation("Relay started. Polling every {PollIntervalMs}ms", _options.PollIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAndPublishAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during relay poll");
            }

            await Task.Delay(_options.PollIntervalMs, stoppingToken);
        }
    }

    public async Task PollAndPublishAsync(CancellationToken ct)
    {
        var channel = _channel ?? throw new InvalidOperationException("Relay has not been initialized.");

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

                var messageId = evt.Id.ToString();
                var props = channel.CreateBasicProperties();
                props.Persistent = true;
                props.MessageId = messageId;
                props.Type = evt.Type;

                _returnedMessages.TryRemove(messageId, out _);
                channel.BasicPublish(
                    RabbitMqTopology.ExchangeName,
                    evt.Type,
                    mandatory: true,
                    basicProperties: props,
                    body: body);

                var confirmed = channel.WaitForConfirms(TimeSpan.FromSeconds(5));
                var returned = _returnedMessages.TryRemove(messageId, out _);
                if (!confirmed || returned)
                {
                    _logger.LogWarning(
                        "Publish not confirmed or unroutable for outbox event {EventId}; stopping batch to preserve order",
                        evt.Id);
                    break;
                }

                evt.MarkPublished();
                _logger.LogInformation("Published outbox event {EventId} ({EventType})", evt.Id, evt.Type);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish outbox event {EventId}; stopping batch to preserve order", evt.Id);
                break;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}