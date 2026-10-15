namespace Wallet.Infrastructure.RateLimiting;

public class RateLimiterOptions
{
    public int MaxRequests { get; set; } = 100;
    public int WindowSeconds { get; set; } = 60;
}