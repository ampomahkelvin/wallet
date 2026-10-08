namespace Wallet.Domain;

public enum WalletKind { User = 1, System = 2 }

public class Wallet
{
    private Wallet() { }

    public Guid Id { get; private set; }
    public WalletKind Kind { get; private set; }
    public string? OwnerId { get; private set; }
    public string Currency { get; private set; } = default!;
    public long Balance { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Wallet CreateUser(string ownerId, string currency)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("Owner is required.");
        if (currency is not { Length: 3 })
            throw new DomainException("Currency must be a 3-letter ISO code.");

        return new Wallet
        {
            Id = Guid.NewGuid(),
            Kind = WalletKind.User,
            OwnerId = ownerId,
            Currency = currency.ToUpperInvariant(),
            Balance = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static Wallet CreateSystem(string currency)
    {
        if (currency is not { Length: 3 })
            throw new DomainException("Currency must be a 3-letter ISO code.");

        return new Wallet
        {
            Id = Guid.NewGuid(),
            Kind = WalletKind.System,
            OwnerId = null,
            Currency = currency.ToUpperInvariant(),
            Balance = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public void ApplyDelta(long delta)
    {
        var newBalance = Balance + delta;
        if (Kind == WalletKind.User && newBalance < 0)
            throw new DomainException("Insufficient funds.");
        Balance = newBalance;
    }
}