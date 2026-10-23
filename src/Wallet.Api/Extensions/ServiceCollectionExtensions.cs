using Microsoft.AspNetCore.Authentication;
using Wallet.Api.Authentication;
using Wallet.Api.Middleware;
using Wallet.Infrastructure;

namespace Wallet.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiServices(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddControllers();
        services.AddOpenApi();
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddAuthentication(ApiKeyAuthenticationDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationDefaults.Scheme, null);
        services.AddAuthorization(options =>
            options.AddPolicy("Admin", policy => policy.RequireRole("admin")));

        services.AddInfrastructure(config);

        return services;
    }
}