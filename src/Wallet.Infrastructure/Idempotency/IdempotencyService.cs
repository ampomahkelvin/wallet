using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Idempotency;

public class IdempotencyService(
    IDbContextFactory<WalletDbContext> contextFactory,
    ILogger<IdempotencyService> logger) : IIdempotencyService
{
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(24);

    public async Task<(TResponse Body, short Status, bool Replayed)> HandleAsync<TResponse>(
        string clientId,
        string key,
        byte[] requestHash,
        Func<WalletDbContext, Task<(TResponse Body, short Status)>> execute,
        CancellationToken ct = default) where TResponse : class
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        while (true)
        {
            var now = DateTimeOffset.UtcNow;

            var claimed = await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO idempotency_keys (""ClientId"", ""Key"", ""RequestHash"", ""ExpiresAt"")
                VALUES ({clientId}, {key}, {requestHash}, {now.Add(KeyTtl)})
                ON CONFLICT (""ClientId"", ""Key"") DO NOTHING", ct);

            if (claimed == 1)
                break;

            var existing = await db.IdempotencyKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.ClientId == clientId && k.Key == key, ct);

            if (existing is null)
                continue;

            if (!existing.RequestHash.SequenceEqual(requestHash))
                throw new IdempotencyKeyReusedException("Idempotency key reused with a different payload.");

            if (existing.ExpiresAt < DateTimeOffset.UtcNow)
            {
                var reclaimed = await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE idempotency_keys
                    SET ""RequestHash"" = {requestHash},
                        ""ResponseStatus"" = NULL,
                        ""ResponseBody"" = NULL,
                        ""ExpiresAt"" = {now.Add(KeyTtl)}
                    WHERE ""ClientId"" = {clientId} AND ""Key"" = {key} AND ""ExpiresAt"" < {now}", ct);

                if (reclaimed > 0)
                    break;

                continue;
            }

            if (existing.ResponseBody is not null)
            {
                var cached = JsonSerializer.Deserialize<TResponse>(existing.ResponseBody);
                if (cached is not null)
                {
                    logger.LogInformation("Replaying idempotency key {Key} for client {ClientId}", key, clientId);
                    return (cached, existing.ResponseStatus ?? 200, true);
                }
            }

            throw new ConflictException("Idempotency key is being processed or has no cached response.");
        }

        var (response, status) = await execute(db);

        var json = JsonSerializer.Serialize(response);
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE idempotency_keys
            SET ""ResponseStatus"" = {status}, ""ResponseBody"" = {json}
            WHERE ""ClientId"" = {clientId} AND ""Key"" = {key}", ct);

        await tx.CommitAsync(ct);
        return (response, status, false);
    }
}