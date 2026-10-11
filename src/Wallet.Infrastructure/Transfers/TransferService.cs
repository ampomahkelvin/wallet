using Microsoft.EntityFrameworkCore;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Transfers;

public class TransferService(IDbContextFactory<WalletDbContext> contextFactory) : ITransferService
{
    public async Task<Transfer> ExecuteAsync(
        TransferType type,
        Guid sourceWalletId,
        Guid destinationWalletId,
        string currency,
        long amount,
        Guid? reversesTransferId = null,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new DomainException("Amount must be positive.");
        if (sourceWalletId == destinationWalletId)
            throw new DomainException("Source and destination wallets must be different.");

        currency = currency.ToUpperInvariant();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var walletIds = new[] { sourceWalletId, destinationWalletId }.OrderBy(id => id).ToArray();

        var wallets = await db.Wallets
            .FromSqlInterpolated($@"
                SELECT ""Id"", ""Kind"", ""OwnerId"", ""Currency"", ""Balance"", ""CreatedAt""
                FROM wallets
                WHERE ""Id"" = ANY({walletIds})
                ORDER BY ""Id""
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

        var transfer = Transfer.Create(type, sourceWalletId, destinationWalletId, currency, amount, reversesTransferId);

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
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Transfers
            .AsNoTracking()
            .Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<Transfer> ReverseAsync(Guid transferId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var original = await db.Transfers
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);

        if (original is null)
            throw new DomainException("Transfer not found.");

        if (original.Type == TransferType.Reversal)
            throw new DomainException("A reversal cannot be reversed.");

        var alreadyReversed = await db.Transfers
            .AnyAsync(t => t.ReversesTransferId == transferId, ct);

        if (alreadyReversed)
            throw new DomainException("Transfer has already been reversed.");

        return await ExecuteAsync(
            TransferType.Reversal,
            original.DestinationWalletId,
            original.SourceWalletId,
            original.Currency,
            original.Amount,
            transferId,
            ct);
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetWalletEntriesAsync(
        Guid walletId,
        long? before = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 100)
            throw new DomainException("Limit must be between 1 and 100.");

        await using var db = await contextFactory.CreateDbContextAsync(ct);
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
