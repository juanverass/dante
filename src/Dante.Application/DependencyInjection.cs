using Dante.Application.Mapeamento;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dante.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMapsterTypeAdapter>(_ =>
        {
            var configuracao = new ConfiguracaoMapeamento();
            MapeamentosDaApplication.Registrar(configuracao);
            return configuracao.Concluir();
        });
        return services;
    }

    // Ponto de composição explícita para módulos, sem duplicação nos hosts.
    public static IServiceCollection AddMapeamentos(this IServiceCollection services,
        Action<ConfiguracaoMapeamento> registrar)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registrar);
        services.AddSingleton(registrar);
        services.Replace(ServiceDescriptor.Singleton<IMapsterTypeAdapter>(provider =>
        {
            var configuracao = new ConfiguracaoMapeamento();
            MapeamentosDaApplication.Registrar(configuracao);
            foreach (var registro in provider.GetServices<Action<ConfiguracaoMapeamento>>()) registro(configuracao);
            return configuracao.Concluir();
        }));
        return services;
    }
}
