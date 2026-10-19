using Microsoft.EntityFrameworkCore;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Accounts;

public class AccountService(IDbContextFactory<WalletDbContext> contextFactory) : IAccountService
{
    public async Task<Wallet.Domain.Wallet> OpenAsync(string ownerId, string currency, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var wallet = Wallet.Domain.Wallet.CreateUser(ownerId, currency);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(ct);
        return wallet;
    }

    public async Task<Wallet.Domain.Wallet> OpenInTransactionAsync(WalletDbContext db, string ownerId, string currency, CancellationToken ct)
    {
        var wallet = Wallet.Domain.Wallet.CreateUser(ownerId, currency);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(ct);
        return wallet;
    }

    public async Task<Wallet.Domain.Wallet?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    public async Task<Wallet.Domain.Wallet?> GetAsync(WalletDbContext db, Guid id, CancellationToken ct)
        => await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task<Wallet.Domain.Wallet?> GetSystemWalletAsync(string currency, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Wallets.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Kind == WalletKind.System && w.Currency == currency, ct);
    }

    public async Task<Wallet.Domain.Wallet?> GetSystemWalletAsync(WalletDbContext db, string currency, CancellationToken ct)
        => await db.Wallets.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Kind == WalletKind.System && w.Currency == currency, ct);
}