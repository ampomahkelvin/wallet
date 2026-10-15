using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Wallet.Infrastructure.RateLimiting;

namespace Wallet.Api.Middleware;

public class RateLimitingMiddleware(RequestDelegate next, IRateLimiter rateLimiter, ILogger<RateLimitingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "default";
        var route = context.Request.Path;

        var allowed = await rateLimiter.IsAllowedAsync(clientId, route);

        if (!allowed)
        {
            logger.LogWarning("Rate limit exceeded for client {ClientId} on {Route}", clientId, route);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsync("Rate limit exceeded");
            return;
        }

        await next(context);
    }
}