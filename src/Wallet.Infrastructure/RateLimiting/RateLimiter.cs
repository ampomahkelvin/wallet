using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Wallet.Infrastructure.Observability;

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
            {
                LedgerMetrics.RateLimiterRejections.Inc();
                return Task.FromResult(false);
            }

            timestamps.Enqueue(now);
            return Task.FromResult(true);
        }
    }
}

public class RedisRateLimiter : IRateLimiter
{
    private const string Script = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
          redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        return current
        """;

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
            var windowStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)_window.TotalSeconds;
            var key = $"rl:{clientId}:{route}:{windowStart}";

            var count = (long)await db.ScriptEvaluateAsync(Script, new RedisKey[] { key }, new RedisValue[] { (int)_window.TotalSeconds + 5 });

            if (count > _maxRequests)
            {
                LedgerMetrics.RateLimiterRejections.Inc();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable, failing open");
            LedgerMetrics.RateLimiterFailOpen.Inc();
            return await _fallback.IsAllowedAsync(clientId, route, ct);
        }
    }
}