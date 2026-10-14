namespace Wallet.Domain;

public class ConsumerProcessedEvent
{
    private ConsumerProcessedEvent() { }

    public long EventId { get; private set; }
    public string ConsumerName { get; private set; } = default!;
    public DateTimeOffset ProcessedAt { get; private set; }

    public static ConsumerProcessedEvent Create(long eventId, string consumerName)
    {
        return new ConsumerProcessedEvent
        {
            EventId = eventId,
            ConsumerName = consumerName,
            ProcessedAt = DateTimeOffset.UtcNow
        };
    }
}