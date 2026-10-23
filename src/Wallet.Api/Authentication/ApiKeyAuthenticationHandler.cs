using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Wallet.Infrastructure.Accounts;

namespace Wallet.Api.Authentication;

public static class ApiKeyAuthenticationDefaults
{
    public const string Scheme = "ApiKey";
}

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyProvider apiKeys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!apiKeys.TryGetClientId(apiKey, out var clientId) || string.IsNullOrWhiteSpace(clientId))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, clientId),
            new Claim(ClaimTypes.Name, clientId)
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
