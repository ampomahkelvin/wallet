namespace Wallet.Domain;

public enum TransferType { Transfer = 1, Deposit = 2, Withdrawal = 3, Reversal = 4 }

public record TransferLine(Guid WalletId, long Amount);

public class Transfer
{
    private readonly List<LedgerEntry> _entries = [];

    private Transfer() { }

    public Guid Id { get; private set; }
    public TransferType Type { get; private set; }
    public Guid SourceWalletId { get; private set; }
    public Guid DestinationWalletId { get; private set; }
    public string Currency { get; private set; } = default!;
    public long Amount { get; private set; }
    public Guid? ReversesTransferId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<LedgerEntry> Entries => _entries;

    public static Transfer Create(
        TransferType type,
        Guid sourceWalletId,
        Guid destinationWalletId,
        string currency,
        long amount,
        Guid? reversesTransferId = null)
    {
        if (amount <= 0)
            throw new DomainException("Amount must be positive.");
        if (sourceWalletId == destinationWalletId)
            throw new DomainException("Source and destination wallets must be different.");
        if (type == TransferType.Reversal && reversesTransferId == null)
            throw new DomainException("Reversal requires a transfer to reverse.");
        if (type != TransferType.Reversal && reversesTransferId != null)
            throw new DomainException("Only reversals can reference another transfer.");

        var tx = new Transfer
        {
            Id = Guid.NewGuid(),
            Type = type,
            SourceWalletId = sourceWalletId,
            DestinationWalletId = destinationWalletId,
            Currency = currency.ToUpperInvariant(),
            Amount = amount,
            ReversesTransferId = reversesTransferId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        tx._entries.Add(new LedgerEntry(tx.Id, sourceWalletId, -amount, 0));
        tx._entries.Add(new LedgerEntry(tx.Id, destinationWalletId, amount, 0));

        return tx;
    }

    public void SetEntryBalances(long sourceBalanceAfter, long destinationBalanceAfter)
    {
        var srcEntry = _entries.First(e => e.WalletId == SourceWalletId);
        var dstEntry = _entries.First(e => e.WalletId == DestinationWalletId);
        srcEntry.SetBalanceAfter(sourceBalanceAfter);
        dstEntry.SetBalanceAfter(destinationBalanceAfter);
    }
}