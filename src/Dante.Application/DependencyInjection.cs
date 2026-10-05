using Microsoft.Extensions.DependencyInjection;

namespace Dante.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Casos de uso e ports serão registrados pelas respectivas issues.
        return services;
    }
}
