namespace Wallet.Domain;

public enum TransactionType { Funding = 1, Transfer = 2, Withdrawal = 3, Reversal = 4 }
public enum TransactionStatus { Posted = 1, Reversed = 2 }

public record EntryLine(Guid AccountId, EntryDirection Direction, long Amount);

public class LedgerTransaction
{
    private readonly List<LedgerEntry> _entries = [];

    private LedgerTransaction() { } // EF Core

    public Guid Id { get; private set; }
    public TransactionType Type { get; private set; }
    public TransactionStatus Status { get; private set; }
    public string Reference { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<LedgerEntry> Entries => _entries;

    /// The only way to create a transaction, so the invariant is always enforced.
    public static LedgerTransaction Post(TransactionType type, string reference, IReadOnlyCollection<EntryLine> lines)
    {
        if (lines.Count < 2)
            throw new DomainException("A transaction needs at least two entries.");
        if (lines.Any(l => l.Amount <= 0))
            throw new DomainException("Entry amounts must be positive.");

        var debits = lines.Where(l => l.Direction == EntryDirection.Debit).Sum(l => l.Amount);
        var credits = lines.Where(l => l.Direction == EntryDirection.Credit).Sum(l => l.Amount);
        if (debits != credits)
            throw new DomainException($"Unbalanced transaction: debits {debits} != credits {credits}.");

        var tx = new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            Type = type,
            Status = TransactionStatus.Posted,
            Reference = reference,
            CreatedAt = DateTimeOffset.UtcNow
        };

        foreach (var l in lines)
            tx._entries.Add(new LedgerEntry(tx.Id, l.AccountId, l.Direction, l.Amount));

        return tx;
    }
}