using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Messaging;

public class ConsumerService : BackgroundService
{
    private readonly ILogger<ConsumerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnection _connection;
    private readonly EventDispatcher _dispatcher;
    private const string ConsumerName = "wallet-consumer";

    public ConsumerService(
        ILogger<ConsumerService> logger,
        IServiceProvider serviceProvider,
        IConnection connection,
        EventDispatcher dispatcher)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _connection = connection;
        _dispatcher = dispatcher;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = _connection.CreateModel();
        channel.BasicQos(0, 10, false);

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.Received += async (model, ea) =>
        {
            try
            {
                var eventId = long.Parse(ea.BasicProperties.MessageId);
                var eventType = ea.BasicProperties.Type;
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

                await _dispatcher.DispatchAsync(eventType, payload);

                db.ConsumerProcessedEvents.Add(ConsumerProcessedEvent.Create(eventId, ConsumerName));
                await db.SaveChangesAsync(stoppingToken);
                await tx.CommitAsync(stoppingToken);

                channel.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message {DeliveryTag}", ea.DeliveryTag);
                channel.BasicNack(ea.DeliveryTag, false, !ea.Redelivered);
            }
        };

        channel.BasicConsume(RabbitMqTopology.MainQueueName, false, consumer);
        _logger.LogInformation("Consumer started on queue {QueueName}", RabbitMqTopology.MainQueueName);

        return Task.CompletedTask;
    }
}