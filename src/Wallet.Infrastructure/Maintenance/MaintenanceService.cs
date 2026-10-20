using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wallet.Infrastructure.Observability;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Maintenance;

public class MaintenanceService(
    ILogger<MaintenanceService> logger,
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Options.IOptions<MaintenanceOptions> options) : BackgroundService
{
    private readonly MaintenanceOptions _options = options.Value;
    private static readonly long AdvisoryLockKey = 424242;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Maintenance worker started. Running every {IntervalMinutes}min", _options.IntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during maintenance run");
            }

            await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken);
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var gotLock = await db.Database
            .SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock({0}) AS \"Value\"", AdvisoryLockKey)
            .FirstOrDefaultAsync(ct);

        if (gotLock != true)
        {
            logger.LogInformation("Another maintenance instance is running. Skipping.");
            await tx.RollbackAsync(ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var deletedKeys = await db.IdempotencyKeys
            .Where(k => k.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("Deleted {Count} expired idempotency keys", deletedKeys);

        var deletedEvents = await db.OutboxEvents
            .Where(e => e.PublishedAt < now.AddDays(-7))
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("Deleted {Count} old published outbox events", deletedEvents);

        var deletedProcessed = await db.ConsumerProcessedEvents
            .Where(e => e.ProcessedAt < now.AddDays(-30))
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("Deleted {Count} old processed events", deletedProcessed);

        var violations = await ReconciliationService.FindViolationsAsync(db, ct);
        LedgerMetrics.InvariantViolations.Set(violations.Count);

        if (violations.Count > 0)
            logger.LogWarning("Ledger invariant violations detected for wallets: {Wallets}", string.Join(", ", violations));

        await ReconciliationService.RefreshOutboxStatsAsync(db, ct);

        await tx.CommitAsync(ct);
    }
}