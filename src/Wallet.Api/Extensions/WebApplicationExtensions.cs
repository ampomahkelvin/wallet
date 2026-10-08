using Microsoft.EntityFrameworkCore;
using Wallet.Domain;
using Wallet.Infrastructure;
using Wallet.Infrastructure.Persistence;

namespace Wallet.Api.Extensions;

public static class WebApplicationExtensions
{
    public static async Task<WebApplication> UseApiPipelineAsync(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            await app.Services.MigrateDatabaseAsync(); // dev only; run migrations as a deploy step in prod
        }

        await SeedSystemWalletsAsync(app.Services);

        app.UseExceptionHandler();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
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