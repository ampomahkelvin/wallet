namespace Wallet.Domain;

public class IdempotencyKey
{
    private IdempotencyKey() { }

    public string ClientId { get; private set; } = default!;
    public string Key { get; private set; } = default!;
    public byte[] RequestHash { get; private set; } = default!;
    public short? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public static IdempotencyKey Claim(string clientId, string key, byte[] requestHash, TimeSpan ttl)
    {
        return new IdempotencyKey
        {
            ClientId = clientId,
            Key = key,
            RequestHash = requestHash,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl)
        };
    }

    public void SetResponse(short status, string body)
    {
        ResponseStatus = status;
        ResponseBody = body;
    }

    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
}