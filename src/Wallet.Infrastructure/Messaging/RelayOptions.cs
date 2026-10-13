namespace Wallet.Infrastructure.Messaging;

public class RelayOptions
{
    public int PollIntervalMs { get; set; } = 5000;
    public int BatchSize { get; set; } = 100;
    public int MaxRetryCount { get; set; } = 3;
}