using System.Text.Json;

namespace Wallet.Domain;

public class OutboxEvent
{
    private OutboxEvent() { }

    public long Id { get; private set; }
    public Guid TransferId { get; private set; }
    public string Type { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    public static OutboxEvent Create(Guid transferId, string type, object payload)
    {
        return new OutboxEvent
        {
            TransferId = transferId,
            Type = type,
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public void MarkPublished()
    {
        PublishedAt = DateTimeOffset.UtcNow;
    }
}