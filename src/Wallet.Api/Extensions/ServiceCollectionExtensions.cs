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

        services.AddInfrastructure(config);

        // Later: services.AddObservability(config); services.AddIdempotency();

        return services;
    }
}