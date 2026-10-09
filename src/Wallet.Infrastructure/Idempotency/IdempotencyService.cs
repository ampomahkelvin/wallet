using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wallet.Domain;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Idempotency;

public class IdempotencyService(WalletDbContext db) : IIdempotencyService
{
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(24);

    public async Task<(T Result, bool Replayed)> HandleAsync<T>(
        string clientId,
        string key,
        byte[] requestHash,
        Func<Task<T>> execute,
        CancellationToken ct = default) where T : class
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var claimed = await db.IdempotencyKeys
            .FromSqlInterpolated($@"
                INSERT INTO idempotency_keys (client_id, key, request_hash, expires_at)
                VALUES ({clientId}, {key}, {requestHash}, now() + interval '24 hours')
                ON CONFLICT (client_id, key) DO NOTHING
                RETURNING key")
            .ToListAsync(ct);

        if (claimed.Count == 0)
        {
            var existing = await db.IdempotencyKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.ClientId == clientId && k.Key == key, ct);

            if (existing is not null)
            {
                if (existing.RequestHash.SequenceEqual(requestHash))
                {
                    if (existing.ResponseBody is not null)
                    {
                        var replayed = JsonSerializer.Deserialize<T>(existing.ResponseBody);
                        if (replayed is not null)
                            return (replayed, true);
                    }
                }
                else
                {
                    throw new DomainException("Idempotency key reused with different payload.");
                }
            }

            var newKey = IdempotencyKey.Claim(clientId, key, requestHash, KeyTtl);
            db.IdempotencyKeys.Add(newKey);
            await db.SaveChangesAsync(ct);
        }

        var executed = await execute();

        var responseBody = JsonSerializer.Serialize(executed);
        await db.IdempotencyKeys
            .Where(k => k.ClientId == clientId && k.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(k => k.ResponseStatus, (short)200)
                .SetProperty(k => k.ResponseBody, responseBody), ct);

        await tx.CommitAsync(ct);
        return (executed, false);
    }
}
