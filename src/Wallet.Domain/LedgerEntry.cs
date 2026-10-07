namespace Wallet.Domain;

public enum EntryDirection { Debit = 1, Credit = 2 }

/// Append-only. Never update or delete; correct mistakes with a reversing transaction.
public class LedgerEntry
{
    private LedgerEntry() { } // EF Core

    internal LedgerEntry(Guid transactionId, Guid accountId, EntryDirection direction, long amount)
    {
        Id = Guid.NewGuid();
        TransactionId = transactionId;
        AccountId = accountId;
        Direction = direction;
        Amount = amount;
    }

    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public Guid AccountId { get; private set; }
    public EntryDirection Direction { get; private set; }
    public long Amount { get; private set; } // minor units, always positive
}