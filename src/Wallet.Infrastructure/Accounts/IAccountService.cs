using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Accounts;

public interface IAccountService
{
    Task<Wallet.Domain.Wallet> OpenAsync(string ownerId, string currency, CancellationToken ct);

    Task<Wallet.Domain.Wallet> OpenInTransactionAsync(WalletDbContext db, string ownerId, string currency, CancellationToken ct);

    Task<Wallet.Domain.Wallet?> GetAsync(Guid id, string clientId, CancellationToken ct);

    Task<Wallet.Domain.Wallet?> GetAsync(WalletDbContext db, Guid id, string clientId, CancellationToken ct);

    Task<Wallet.Domain.Wallet?> GetSystemWalletAsync(string currency, CancellationToken ct);

    Task<Wallet.Domain.Wallet?> GetSystemWalletAsync(WalletDbContext db, string currency, CancellationToken ct);
}