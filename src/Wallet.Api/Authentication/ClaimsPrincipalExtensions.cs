using System.Security.Claims;
using Wallet.Domain;

namespace Wallet.Api.Authentication;

public static class ClaimsPrincipalExtensions
{
    public static string GetClientId(this ClaimsPrincipal principal)
        => principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedException("Authenticated client is missing.");
}
