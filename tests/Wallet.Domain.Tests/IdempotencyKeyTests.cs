using FluentAssertions;
using Wallet.Domain;

namespace Wallet.Domain.Tests;

public class IdempotencyKeyTests
{
    [Fact]
    public void Claim_WithValidInput_ReturnsKey()
    {
        var hash = new byte[32];
        var key = IdempotencyKey.Claim("client-1", "key-1", hash, TimeSpan.FromHours(24));

        key.ClientId.Should().Be("client-1");
        key.Key.Should().Be("key-1");
        key.RequestHash.Should().BeEquivalentTo(hash);
        key.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        key.ResponseStatus.Should().BeNull();
        key.ResponseBody.Should().BeNull();
    }

    [Fact]
    public void SetResponse_UpdatesStatusAndBody()
    {
        var hash = new byte[32];
        var key = IdempotencyKey.Claim("client-1", "key-1", hash, TimeSpan.FromHours(24));

        key.SetResponse(200, "{\"id\":\"123\"}");

        key.ResponseStatus.Should().Be(200);
        key.ResponseBody.Should().Be("{\"id\":\"123\"}");
    }

    [Fact]
    public void IsExpired_WhenKeyExpired_ReturnsTrue()
    {
        var hash = new byte[32];
        var key = IdempotencyKey.Claim("client-1", "key-1", hash, TimeSpan.FromHours(-1));

        key.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_WhenKeyNotExpired_ReturnsFalse()
    {
        var hash = new byte[32];
        var key = IdempotencyKey.Claim("client-1", "key-1", hash, TimeSpan.FromHours(24));

        key.IsExpired.Should().BeFalse();
    }
}