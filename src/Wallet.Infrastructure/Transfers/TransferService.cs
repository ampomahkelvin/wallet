using Microsoft.EntityFrameworkCore;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Transfers;

public class TransferService(WalletDbContext db) : ITransferService
{
    public async Task<Transfer> ExecuteAsync(
        TransferType type,
        Guid sourceWalletId,
        Guid destinationWalletId,
        string currency,
        long amount,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new DomainException("Amount must be positive.");
        if (sourceWalletId == destinationWalletId)
            throw new DomainException("Source and destination wallets must be different.");

        currency = currency.ToUpperInvariant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var walletIds = new[] { sourceWalletId, destinationWalletId }.OrderBy(id => id).ToArray();

        var wallets = await db.Wallets
            .FromSqlInterpolated($@"
                SELECT id, kind, owner_id, currency, balance, created_at
                FROM wallets
                WHERE id = ANY({walletIds})
                ORDER BY id
                FOR UPDATE")
            .ToListAsync(ct);

        if (wallets.Count != 2)
            throw new DomainException("One or both wallets not found.");

        var source = wallets.Find(w => w.Id == sourceWalletId)
            ?? throw new DomainException("Source wallet not found.");
        var dest = wallets.Find(w => w.Id == destinationWalletId)
            ?? throw new DomainException("Destination wallet not found.");

        if (source.Currency != currency || dest.Currency != currency)
            throw new DomainException("Currency mismatch.");

        if (source.Kind == WalletKind.User && source.Balance < amount)
            throw new DomainException("Insufficient funds.");

        var transfer = Transfer.Create(type, sourceWalletId, destinationWalletId, currency, amount);

        source.ApplyDelta(-amount);
        dest.ApplyDelta(amount);

        transfer.SetEntryBalances(source.Balance, dest.Balance);

        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return transfer;
    }

    public async Task<Transfer?> GetAsync(Guid id, CancellationToken ct = default)
    {
        return await db.Transfers
            .AsNoTracking()
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetWalletEntriesAsync(
        Guid walletId,
        long? before = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 100)
            throw new DomainException("Limit must be between 1 and 100.");

        var query = db.LedgerEntries
            .AsNoTracking()
            .Where(e => e.WalletId == walletId);

        if (before.HasValue)
            query = query.Where(e => e.Id < before.Value);

        return await query
            .OrderByDescending(e => e.Id)
            .Take(limit + 1)
            .ToListAsync(ct);
    }
}