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

public class ConsumerService : BackgroundService
{
    private readonly ILogger<ConsumerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly EventDispatcher _dispatcher;
    private readonly ConsumerOptions _options;
    private readonly TimeSpan? _retryDelay;
    private readonly ConcurrentDictionary<string, byte> _returnedRetries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _retryPublishLock = new(1, 1);
    private const string ConsumerName = "wallet-consumer";
    private IModel? _retryChannel;

    public ConsumerService(
        ILogger<ConsumerService> logger,
        IServiceProvider serviceProvider,
        RabbitMqConnectionProvider connectionProvider,
        EventDispatcher dispatcher,
        IOptions<ConsumerOptions> options,
        TimeSpan? retryDelay = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _connectionProvider = connectionProvider;
        _dispatcher = dispatcher;
        _options = options.Value;
        _retryDelay = retryDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await _connectionProvider.GetConnectionAsync(stoppingToken);
        var channel = connection.CreateModel();
        RabbitMqTopology.DeclareTopology(channel, _retryDelay);
        channel.BasicQos(0, (ushort)_options.PrefetchCount, false);

        _retryChannel = connection.CreateModel();
        _retryChannel.ConfirmSelect();
        _retryChannel.BasicReturn += OnRetryReturned;

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.Received += async (model, ea) =>
        {
            var retryCount = GetIntHeader(ea.BasicProperties.Headers, RabbitMqTopology.RetryCountHeader);
            var originalType = GetStringHeader(ea.BasicProperties.Headers, RabbitMqTopology.OriginalTypeHeader)
                ?? ea.BasicProperties.Type;

            try
            {
                var eventId = long.Parse(ea.BasicProperties.MessageId);
                var payload = Encoding.UTF8.GetString(ea.Body.ToArray());

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();

                await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);

                var existing = await db.ConsumerProcessedEvents
                    .AnyAsync(e => e.EventId == eventId && e.ConsumerName == ConsumerName, stoppingToken);

                if (existing)
                {
                    channel.BasicAck(ea.DeliveryTag, false);
                    await tx.CommitAsync(stoppingToken);
                    return;
                }

                await _dispatcher.DispatchAsync(originalType, payload);

                db.ConsumerProcessedEvents.Add(ConsumerProcessedEvent.Create(eventId, ConsumerName));
                await db.SaveChangesAsync(stoppingToken);
                await tx.CommitAsync(stoppingToken);

                channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process message {DeliveryTag} (retry {RetryCount})", ea.DeliveryTag, retryCount);

                if (retryCount >= _options.MaxRetryCount)
                {
                    channel.BasicNack(ea.DeliveryTag, false, false);
                    return;
                }

                try
                {
                    await PublishRetryAsync(
                        ea.Body.ToArray(),
                        ea.BasicProperties.MessageId,
                        originalType,
                        retryCount,
                        stoppingToken);

                    channel.BasicAck(ea.DeliveryTag, false);
                }
                catch (Exception publishEx)
                {
                    _logger.LogError(publishEx, "Failed to schedule retry for message {DeliveryTag}", ea.DeliveryTag);
                    channel.BasicNack(ea.DeliveryTag, false, false);
                }
            }
        };

        channel.BasicConsume(RabbitMqTopology.MainQueueName, false, consumer);
        _logger.LogInformation("Consumer started on queue {QueueName}", RabbitMqTopology.MainQueueName);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private void OnRetryReturned(object? sender, BasicReturnEventArgs e)
    {
        if (e.BasicProperties.CorrelationId is { } correlationId)
            _returnedRetries[correlationId] = 0;

        _logger.LogWarning(
            "Retry message returned: reply code {ReplyCode}, exchange {Exchange}, routing key {RoutingKey}",
            e.ReplyCode, e.Exchange, e.RoutingKey);
    }

    private async Task PublishRetryAsync(
        byte[] body,
        string? messageId,
        string originalType,
        int retryCount,
        CancellationToken ct)
    {
        var retryChannel = _retryChannel
            ?? throw new InvalidOperationException("Retry publisher channel has not been initialized.");

        await _retryPublishLock.WaitAsync(ct);
        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            var props = retryChannel.CreateBasicProperties();
            props.Persistent = true;
            props.MessageId = messageId;
            props.Type = originalType;
            props.CorrelationId = correlationId;
            props.Headers = new Dictionary<string, object>
            {
                [RabbitMqTopology.RetryCountHeader] = retryCount + 1,
                [RabbitMqTopology.OriginalTypeHeader] = originalType
            };

            _returnedRetries.TryRemove(correlationId, out _);
            retryChannel.BasicPublish(
                RabbitMqTopology.RetryExchangeName,
                originalType,
                mandatory: true,
                basicProperties: props,
                body: body);

            var confirmed = retryChannel.WaitForConfirms(TimeSpan.FromSeconds(5));
            var returned = _returnedRetries.TryRemove(correlationId, out _);
            if (!confirmed || returned)
                throw new InvalidOperationException(
                    $"Retry publish was not confirmed or was unroutable for message {messageId}.");
        }
        finally
        {
            _retryPublishLock.Release();
        }
    }

    private static string? GetStringHeader(IDictionary<string, object>? headers, string key)
    {
        if (headers is null || !headers.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => value.ToString()
        };
    }

    private static int GetIntHeader(IDictionary<string, object>? headers, string key)
    {
        var value = GetStringHeader(headers, key);
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }
}