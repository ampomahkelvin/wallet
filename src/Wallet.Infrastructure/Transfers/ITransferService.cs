using Wallet.Domain;

namespace Wallet.Infrastructure.Transfers;

public interface ITransferService
{
    Task<Transfer> ExecuteAsync(
        TransferType type,
        Guid sourceWalletId,
        Guid destinationWalletId,
        string currency,
        long amount,
        CancellationToken ct = default);

    Task<Transfer?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<LedgerEntry>> GetWalletEntriesAsync(
        Guid walletId,
        long? before = null,
        int limit = 50,
        CancellationToken ct = default);
}