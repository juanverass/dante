using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        // Adapters e persistência serão registrados pelas respectivas issues.
        return services;
    }
}
