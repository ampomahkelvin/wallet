namespace Wallet.Infrastructure.Idempotency;

public interface IIdempotencyService
{
    Task<(T Result, bool Replayed)> HandleAsync<T>(
        string clientId,
        string key,
        byte[] requestHash,
        Func<Task<T>> execute,
        CancellationToken ct = default) where T : class;
}