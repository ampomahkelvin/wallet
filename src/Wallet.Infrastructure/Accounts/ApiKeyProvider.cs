using Microsoft.Extensions.Configuration;

namespace Wallet.Infrastructure.Accounts;

public class ApiKeyProvider(IConfiguration configuration) : IApiKeyProvider
{
    private readonly Dictionary<string, string> _keys =
        configuration.GetSection("ApiKeys").Get<Dictionary<string, string>>()
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly HashSet<string> _adminKeys = new(
        configuration.GetSection("AdminApiKeys").Get<string[]>() ?? [],
        StringComparer.Ordinal);

    public bool TryGetClientId(string apiKey, out string clientId)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            clientId = null!;
            return false;
        }

        if (_keys.TryGetValue(apiKey, out var found) && !string.IsNullOrWhiteSpace(found))
        {
            clientId = found;
            return true;
        }

        clientId = null!;
        return false;
    }

    public bool IsAdmin(string apiKey)
        => !string.IsNullOrEmpty(apiKey) && _adminKeys.Contains(apiKey);
}