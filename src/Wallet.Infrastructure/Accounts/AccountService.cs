using Microsoft.EntityFrameworkCore;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Accounts;

public class AccountService(WalletDbContext db) : IAccountService
{
    public async Task<Wallet.Domain.Wallet> OpenAsync(string ownerId, string currency, CancellationToken ct)
    {
        var wallet = Wallet.Domain.Wallet.CreateUser(ownerId, currency);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(ct);
        return wallet;
    }

    public Task<Wallet.Domain.Wallet?> GetAsync(Guid id, CancellationToken ct)
        => db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);

    public Task<Wallet.Domain.Wallet?> GetSystemWalletAsync(string currency, CancellationToken ct)
        => db.Wallets.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Kind == WalletKind.System && w.Currency == currency, ct);
}
