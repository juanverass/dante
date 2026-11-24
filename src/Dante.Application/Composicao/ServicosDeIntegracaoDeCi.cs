using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dante.Application;

// Integração de CI (AD-76, AD-77): o acompanhamento do CI de um PR é sempre registrado e não depende de banco; as portas
// (provedor e identidade de CI) vêm da Infrastructure, que responde NaoConfigurada sem conexão no host.
internal static class ServicosDeIntegracaoDeCi
{
    internal static void Registrar(IServiceCollection services)
    {
        services.TryAddScoped<Dante.Application.IntegracaoDeCi.IAcompanhamentoDeCiAppService>(provider =>
            ActivatorUtilities.CreateInstance<Dante.Application.IntegracaoDeCi.AcompanhamentoDeCiAppService>(provider));
    }
}
