using Wallet.Infrastructure.Persistence;

namespace Wallet.Infrastructure.Idempotency;

public interface IIdempotencyService
{
    Task<(TResponse Body, short Status, bool Replayed)> HandleAsync<TResponse>(
        string clientId,
        string key,
        byte[] requestHash,
        Func<WalletDbContext, Task<(TResponse Body, short Status)>> execute,
        CancellationToken ct = default) where TResponse : class;
}