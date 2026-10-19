using System.Data;
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
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var result = await ExecuteCoreAsync(db, type, sourceWalletId, destinationWalletId, currency, amount, reversesTransferId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<Transfer> ExecuteInTransactionAsync(
        WalletDbContext db,
        TransferType type,
        Guid sourceWalletId,
        Guid destinationWalletId,
        string currency,
        long amount,
        Guid? reversesTransferId = null,
        CancellationToken ct = default)
        => ExecuteCoreAsync(db, type, sourceWalletId, destinationWalletId, currency, amount, reversesTransferId, ct);

    private async Task<Transfer> ExecuteCoreAsync(
        WalletDbContext db,
        TransferType type,
        Guid sourceWalletId,
        Guid destinationWalletId,
        string currency,
        long amount,
        Guid? reversesTransferId,
        CancellationToken ct)
    {
        if (amount <= 0)
            throw new ValidationException("Amount must be positive.");
        if (sourceWalletId == destinationWalletId)
            throw new ValidationException("Source and destination wallets must be different.");

        currency = currency.ToUpperInvariant();

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
            throw new NotFoundException("One or both wallets not found.");

        var source = wallets.Find(w => w.Id == sourceWalletId)
            ?? throw new NotFoundException("Source wallet not found.");
        var dest = wallets.Find(w => w.Id == destinationWalletId)
            ?? throw new NotFoundException("Destination wallet not found.");

        if (source.Currency != currency || dest.Currency != currency)
            throw new ValidationException("Currency mismatch.");

        if (source.Kind == WalletKind.User && source.Balance < amount)
            throw new InsufficientFundsException("Insufficient funds.");

        var transfer = Transfer.Create(type, sourceWalletId, destinationWalletId, currency, amount, reversesTransferId);

        source.ApplyDelta(-amount);
        dest.ApplyDelta(amount);

        transfer.SetEntryBalances(source.Balance, dest.Balance);

        db.Transfers.Add(transfer);

        var eventType = type switch
        {
            TransferType.Transfer => "transfer.completed",
            TransferType.Deposit => "deposit.completed",
            TransferType.Withdrawal => "withdrawal.completed",
            TransferType.Reversal => "transfer.reversed",
            _ => throw new ValidationException($"Unknown transfer type: {type}")
        };

        var outboxEvent = OutboxEvent.Create(transfer.Id, eventType, new
        {
            transfer.Id,
            transfer.Type,
            transfer.SourceWalletId,
            transfer.DestinationWalletId,
            transfer.Currency,
            transfer.Amount,
            transfer.CreatedAt
        });
        db.OutboxEvents.Add(outboxEvent);

        await db.SaveChangesAsync(ct);
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
            throw new NotFoundException("Transfer not found.");

        if (original.Type == TransferType.Reversal)
            throw new ValidationException("A reversal cannot be reversed.");

        var alreadyReversed = await db.Transfers
            .AnyAsync(t => t.ReversesTransferId == transferId, ct);

        if (alreadyReversed)
            throw new ConflictException("Transfer has already been reversed.");

        return await ExecuteInTransactionAsync(
            db,
            TransferType.Reversal,
            original.DestinationWalletId,
            original.SourceWalletId,
            original.Currency,
            original.Amount,
            transferId,
            ct);
    }

    public async Task<Transfer> ReverseInTransactionAsync(WalletDbContext db, Guid transferId, CancellationToken ct = default)
    {
        var original = await db.Transfers
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);

        if (original is null)
            throw new NotFoundException("Transfer not found.");

        if (original.Type == TransferType.Reversal)
            throw new ValidationException("A reversal cannot be reversed.");

        var alreadyReversed = await db.Transfers
            .AnyAsync(t => t.ReversesTransferId == transferId, ct);

        if (alreadyReversed)
            throw new ConflictException("Transfer has already been reversed.");

        return await ExecuteCoreAsync(
            db,
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
            throw new ValidationException("Limit must be between 1 and 100.");

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