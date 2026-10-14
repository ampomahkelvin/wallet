using RabbitMQ.Client;

namespace Wallet.Infrastructure.Messaging;

public static class RabbitMqTopology
{
    public const string ExchangeName = "wallet.events";
    public const string DlxName = "wallet.dlx";
    public const string MainQueueName = "wallet.events.main";
    public const string RetryQueueName = "wallet.events.retry";
    public const string DlqName = "wallet.events.dlq";

    public static void DeclareTopology(IModel channel)
    {
        channel.ExchangeDeclare(ExchangeName, ExchangeType.Topic, durable: true);
        channel.ExchangeDeclare(DlxName, ExchangeType.Topic, durable: true);

        channel.QueueDeclare(DlqName, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(DlqName, DlxName, "transfer.*");
        channel.QueueBind(DlqName, DlxName, "deposit.*");
        channel.QueueBind(DlqName, DlxName, "withdrawal.*");

        channel.QueueDeclare(RetryQueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                { "x-dead-letter-exchange", ExchangeName },
                { "x-message-ttl", 30000 }
            });
        channel.QueueBind(RetryQueueName, ExchangeName, "retry.*");

        channel.QueueDeclare(MainQueueName, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                { "x-dead-letter-exchange", DlxName },
                { "x-dead-letter-routing-key", "transfer.completed" }
            });
        channel.QueueBind(MainQueueName, ExchangeName, "transfer.*");
        channel.QueueBind(MainQueueName, ExchangeName, "deposit.*");
        channel.QueueBind(MainQueueName, ExchangeName, "withdrawal.*");
    }
}