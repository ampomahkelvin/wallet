using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Wallet.Infrastructure.Observability;
using Wallet.Infrastructure.RateLimiting;

namespace Wallet.Api.Middleware;

public class RateLimitingMiddleware(RequestDelegate next, IRateLimiter rateLimiter, ILogger<RateLimitingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Post)
        {
            await next(context);
            return;
        }

        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault() ?? "default";
        var route = NormalizeRoute(context.Request.Path);

        var allowed = await rateLimiter.IsAllowedAsync(clientId, route);

        if (!allowed)
        {
            logger.LogWarning("Rate limit exceeded for client {ClientId} on {Route}", clientId, route);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { code = "RATE_LIMITED", message = "Rate limit exceeded" });
            return;
        }

        await next(context);
    }

    private static string NormalizeRoute(PathString path)
    {
        if (string.IsNullOrEmpty(path.Value))
            return "/";

        var segments = path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = segments.Select(s => Guid.TryParse(s, out _) ? "{id}" : s);
        return "/" + string.Join("/", normalized);
    }
}