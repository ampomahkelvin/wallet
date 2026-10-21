namespace Wallet.Infrastructure.Messaging;

public class ConsumerOptions
{
    public int MaxRetryCount { get; set; } = 3;
    public int PrefetchCount { get; set; } = 10;
}