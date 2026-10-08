using Wallet.Domain;

namespace Wallet.Infrastructure.Accounts;

public interface IAccountService
{
    Task<Wallet.Domain.Wallet> OpenAsync(string ownerId, string currency, CancellationToken ct);
    Task<Wallet.Domain.Wallet?> GetAsync(Guid id, CancellationToken ct);
    Task<Wallet.Domain.Wallet?> GetSystemWalletAsync(string currency, CancellationToken ct);
}
