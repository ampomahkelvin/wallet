using Wallet.Domain;

namespace Wallet.Infrastructure.Accounts;

public interface IAccountService
{
    Task<Account> OpenAsync(string ownerId, string currency, CancellationToken ct);
    Task<Account?> GetAsync(Guid id, CancellationToken ct);
}