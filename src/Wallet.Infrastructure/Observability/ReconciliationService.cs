using Microsoft.EntityFrameworkCore;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Observability;

public static class ReconciliationService
{
    public static async Task<IReadOnlyList<Guid>> FindViolationsAsync(
        WalletDbContext db,
        CancellationToken ct = default)
    {
        return await db.Database
            .SqlQueryRaw<Guid>(@"
                SELECT w.""Id""
                FROM wallets w
                LEFT JOIN (
                    SELECT ""WalletId"", SUM(""Amount"") AS net
                    FROM ledger_entries
                    GROUP BY ""WalletId""
                ) e ON e.""WalletId"" = w.""Id""
                WHERE w.""Balance"" <> COALESCE(e.net, 0)")
            .ToListAsync(ct);
    }

    public static async Task<long> ZeroSumAsync(WalletDbContext db, CancellationToken ct = default)
        => await db.LedgerEntries.SumAsync(e => e.Amount, ct);

    public static async Task<GaugeStats> RefreshOutboxStatsAsync(
        WalletDbContext db,
        CancellationToken ct = default)
    {
        var count = await db.OutboxEvents.CountAsync(e => e.PublishedAt == null, ct);
        var oldest = await db.OutboxEvents
            .Where(e => e.PublishedAt == null)
            .OrderBy(e => e.Id)
            .Select(e => (DateTimeOffset?)e.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var ageSeconds = oldest is null
            ? 0
            : ( DateTimeOffset.UtcNow - oldest.Value ).TotalSeconds;

        LedgerMetrics.OutboxUnpublishedCount.Set(count);
        LedgerMetrics.OutboxOldestUnpublishedSeconds.Set(ageSeconds);

        return new GaugeStats(count, ageSeconds);
    }

    public sealed record GaugeStats(int UnpublishedCount, double OldestUnpublishedSeconds);
}