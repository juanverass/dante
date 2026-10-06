using Dante.Infrastructure.Composicao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Sem configuração, os hosts continuam operando sem banco ou adapters do Brain.
        var connectionString = configuration.GetConnectionString("Dante");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddBanco(connectionString);
            services.AddBrain(configuration);
        }

        services.AddContextos();
        services.AddAgentes();
        return services;
    }
}
