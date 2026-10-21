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
    private const string ConsumerName = "wallet-consumer";

    public ConsumerService(
        ILogger<ConsumerService> logger,
        IServiceProvider serviceProvider,
        RabbitMqConnectionProvider connectionProvider,
        EventDispatcher dispatcher,
        IOptions<ConsumerOptions> options)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _connectionProvider = connectionProvider;
        _dispatcher = dispatcher;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await _connectionProvider.GetConnectionAsync(stoppingToken);
        var channel = connection.CreateModel();
        channel.BasicQos(0, (ushort)_options.PrefetchCount, false);

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
                    var props = channel.CreateBasicProperties();
                    props.Persistent = true;
                    props.MessageId = ea.BasicProperties.MessageId;
                    props.Type = originalType;
                    props.Headers = new Dictionary<string, object>
                    {
                        [RabbitMqTopology.RetryCountHeader] = retryCount + 1,
                        [RabbitMqTopology.OriginalTypeHeader] = originalType
                    };

                    channel.BasicPublish(
                        RabbitMqTopology.RetryExchangeName,
                        RabbitMqTopology.RetryRoutingKeyPrefix + originalType,
                        mandatory: true,
                        basicProperties: props,
                        body: ea.Body.ToArray());

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