using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Wallet.Infrastructure.RateLimiting;

public interface IRateLimiter
{
    Task<bool> IsAllowedAsync(string clientId, string route, CancellationToken ct = default);
}

public class InMemoryRateLimiter(int maxRequests, TimeSpan window) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows = [];

    public Task<bool> IsAllowedAsync(string clientId, string route, CancellationToken ct = default)
    {
        var key = $"{clientId}:{route}";
        var now = DateTimeOffset.UtcNow;
        var windowStart = now - window;

        var timestamps = _windows.GetOrAdd(key, _ => new Queue<DateTimeOffset>());

        lock (timestamps)
        {
            while (timestamps.Count > 0 && timestamps.Peek() < windowStart)
                timestamps.Dequeue();

            if (timestamps.Count >= maxRequests)
                return Task.FromResult(false);

            timestamps.Enqueue(now);
            return Task.FromResult(true);
        }
    }
}

public class RedisRateLimiter : IRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly ILogger<RedisRateLimiter> _logger;
    private readonly InMemoryRateLimiter _fallback;

    public RedisRateLimiter(
        IConnectionMultiplexer redis,
        int maxRequests,
        TimeSpan window,
        ILogger<RedisRateLimiter> logger)
    {
        _redis = redis;
        _maxRequests = maxRequests;
        _window = window;
        _logger = logger;
        _fallback = new InMemoryRateLimiter(maxRequests, window);
    }

    public async Task<bool> IsAllowedAsync(string clientId, string route, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = $"rl:{clientId}:{route}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)_window.TotalSeconds}";
            var count = await db.StringIncrementAsync(key);
            if (count == 1)
                await db.KeyExpireAsync(key, _window.Add(TimeSpan.FromSeconds(5)));
            return count <= _maxRequests;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable, failing open");
            return await _fallback.IsAllowedAsync(clientId, route, ct);
        }
    }
}