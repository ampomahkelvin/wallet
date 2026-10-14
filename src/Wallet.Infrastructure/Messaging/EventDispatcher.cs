using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wallet.Infrastructure.Messaging;

public class EventDispatcher
{
    private readonly ILogger<EventDispatcher> _logger;
    private readonly Dictionary<string, Func<string, Task>> _handlers = [];

    public EventDispatcher(ILogger<EventDispatcher> logger)
    {
        _logger = logger;
    }

    public void Register(string eventType, Func<string, Task> handler)
    {
        _handlers[eventType] = handler;
    }

    public async Task DispatchAsync(string eventType, string payload)
    {
        if (!_handlers.TryGetValue(eventType, out var handler))
        {
            _logger.LogWarning("No handler registered for event type {EventType}", eventType);
            return;
        }

        await handler(payload);
    }
}