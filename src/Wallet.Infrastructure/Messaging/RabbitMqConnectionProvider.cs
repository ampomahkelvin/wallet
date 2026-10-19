using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Wallet.Infrastructure.Messaging;

public class RabbitMqConnectionProvider(string uri, ILogger<RabbitMqConnectionProvider> logger)
{
    private readonly ConnectionFactory _factory = new()
    {
        Uri = new Uri(uri),
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true,
        NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        DispatchConsumersAsync = true
    };

    private IConnection? _connection;

    public bool IsConnected => _connection?.IsOpen == true;

    public async Task<IConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (_connection is { IsOpen: true })
            return _connection;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _connection = _factory.CreateConnection();
                logger.LogInformation("Connected to RabbitMQ at {Uri}", uri);
                return _connection;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RabbitMQ unavailable at {Uri}; retrying in 5s", uri);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        throw new OperationCanceledException("RabbitMQ connection was not established before cancellation.");
    }
}