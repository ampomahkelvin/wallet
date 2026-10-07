namespace Wallet.Domain;

public enum AccountType { UserWallet = 1, SystemFunding = 2, SystemFees = 3 }

public class Account
{
    private Account() { } // EF Core

    public Guid Id { get; private set; }
    public string OwnerId { get; private set; } = default!;
    public string Currency { get; private set; } = default!;
    public AccountType Type { get; private set; }

    /// Cached balance in minor units (pesewas/cents). Ledger entries are the source of truth.
    public long Balance { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Account Open(string ownerId, string currency, AccountType type)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("Owner is required.");
        if (currency is not { Length: 3 })
            throw new DomainException("Currency must be a 3-letter ISO code.");

        return new Account
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Currency = currency.ToUpperInvariant(),
            Type = type,
            Balance = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}