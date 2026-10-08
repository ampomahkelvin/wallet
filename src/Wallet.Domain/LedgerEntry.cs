namespace Wallet.Domain;

public class LedgerEntry
{
    private LedgerEntry() { }

    internal LedgerEntry(Guid transferId, Guid walletId, long amount, long balanceAfter)
    {
        TransferId = transferId;
        WalletId = walletId;
        Amount = amount;
        BalanceAfter = balanceAfter;
    }

    public long Id { get; private set; }
    public Guid TransferId { get; private set; }
    public Guid WalletId { get; private set; }
    public long Amount { get; private set; }
    public long BalanceAfter { get; private set; }

    internal void SetBalanceAfter(long balanceAfter)
    {
        BalanceAfter = balanceAfter;
    }
}