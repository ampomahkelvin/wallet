using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using StackExchange.Redis;
using Wallet.Api.Middleware;
using Wallet.Domain;
using Wallet.Infrastructure;
using Wallet.Infrastructure.Messaging;
using Wallet.Infrastructure.Persistence;
using Wallet.Infrastructure.Observability;

namespace Wallet.Api.Extensions;

public static class WebApplicationExtensions
{
    public static async Task<WebApplication> UseApiPipelineAsync(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
            app.MapOpenApi();

        await app.Services.MigrateDatabaseAsync();
        await SeedSystemWalletsAsync(app.Services);

        app.UseExceptionHandler();
        app.UseHttpMetrics();
        app.UseMetricServer();
        app.UseMiddleware<RateLimitingMiddleware>();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/deep", async (
            WalletDbContext db,
            RabbitMqConnectionProvider rabbit,
            IConnectionMultiplexer redis) =>
        {
            var database = await db.Database.CanConnectAsync();
            var broker = rabbit.IsConnected;
            var cache = redis.IsConnected;
            var healthy = database;
            return Results.Ok(new
            {
                status = healthy ? "ok" : "degraded",
                database = database ? "up" : "down",
                broker = broker ? "up" : "down",
                redis = cache ? "up" : "down"
            });
        });
        app.MapGet("/admin/reconcile", async (WalletDbContext db) =>
        {
            var violations = await ReconciliationService.FindViolationsAsync(db);
            LedgerMetrics.InvariantViolations.Set(violations.Count);
            return Results.Ok(new { violations, status = violations.Count == 0 ? "ok" : "violations_detected" });
        });
        app.MapControllers();

        return app;
    }

    private static async Task SeedSystemWalletsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
        var currencies = new[] { "GHS" };

        foreach (var currency in currencies)
        {
            var exists = await db.Wallets.AnyAsync(w => w.Kind == WalletKind.System && w.Currency == currency);
            if (!exists)
            {
                var systemWallet = Wallet.Domain.Wallet.CreateSystem(currency);
                db.Wallets.Add(systemWallet);
            }
        }

        await db.SaveChangesAsync();
    }
}