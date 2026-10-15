using FluentAssertions;
using Wallet.Infrastructure.RateLimiting;
using Xunit;

namespace Wallet.Infrastructure.Tests;

public class RateLimiterTests
{
    [Fact]
    public async Task InMemoryRateLimiter_WithinLimit_Allows()
    {
        var limiter = new InMemoryRateLimiter(3, TimeSpan.FromSeconds(60));

        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeTrue();
        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeTrue();
        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeTrue();
    }

    [Fact]
    public async Task InMemoryRateLimiter_ExceedsLimit_Rejects()
    {
        var limiter = new InMemoryRateLimiter(2, TimeSpan.FromSeconds(60));

        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeTrue();
        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeTrue();
        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryRateLimiter_DifferentClients_IndependentLimits()
    {
        var limiter = new InMemoryRateLimiter(1, TimeSpan.FromSeconds(60));

        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeTrue();
        (await limiter.IsAllowedAsync("client-2", "route-1")).Should().BeTrue();
        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryRateLimiter_WindowExpires_AllowsAgain()
    {
        var limiter = new InMemoryRateLimiter(1, TimeSpan.FromMilliseconds(100));

        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeTrue();
        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeFalse();

        await Task.Delay(150);

        (await limiter.IsAllowedAsync("client-1", "route-1")).Should().BeTrue();
    }
}