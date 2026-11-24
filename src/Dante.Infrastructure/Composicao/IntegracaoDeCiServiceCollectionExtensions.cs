using Dante.Application.IntegracaoDeCi;
using Dante.Infrastructure.IntegracaoDeCi;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure.Composicao;

internal static class IntegracaoDeCiServiceCollectionExtensions
{
    // Só o contrato nesta fase (#236): a porta responde "não configurada" até existir o primeiro provedor (#237).
    internal static void AddIntegracaoDeCi(this IServiceCollection services) =>
        services.AddSingleton<IProvedorDeCi, ProvedorDeCiNaoConfigurado>();
}
