using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Transfers;

public interface ITransferService
{
    Task<Transfer> ExecuteAsync(
        TransferType type,
        Guid sourceWalletId,
        Guid destinationWalletId,
        string currency,
        long amount,
        string clientId,
        Guid? reversesTransferId = null,
        CancellationToken ct = default);

    Task<Transfer> ExecuteInTransactionAsync(
        WalletDbContext db,
        TransferType type,
        Guid sourceWalletId,
        Guid destinationWalletId,
        string currency,
        long amount,
        string clientId,
        Guid? reversesTransferId = null,
        CancellationToken ct = default);

    Task<Transfer?> GetAsync(Guid id, CancellationToken ct = default);

    Task<Transfer> ReverseAsync(Guid transferId, string clientId, CancellationToken ct = default);

    Task<Transfer> ReverseInTransactionAsync(WalletDbContext db, Guid transferId, string clientId, CancellationToken ct = default);

    Task<IReadOnlyList<LedgerEntry>> GetWalletEntriesAsync(
        Guid walletId,
        long? before = null,
        int limit = 50,
        CancellationToken ct = default);
}