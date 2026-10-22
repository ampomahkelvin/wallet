namespace Wallet.Infrastructure.Accounts;

public interface IApiKeyProvider
{
    bool TryGetClientId(string apiKey, out string clientId);
}