using Wallet.Infrastructure;

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

        app.UseExceptionHandler();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapControllers();

        return app;
    }
}