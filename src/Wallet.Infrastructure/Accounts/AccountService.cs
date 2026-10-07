using Microsoft.EntityFrameworkCore;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Accounts;

public class AccountService(WalletDbContext db) : IAccountService
{
    public async Task<Account> OpenAsync(string ownerId, string currency, CancellationToken ct)
    {
        var account = Account.Open(ownerId, currency, AccountType.UserWallet);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public Task<Account?> GetAsync(Guid id, CancellationToken ct)
        => db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
}