using System.Threading;
using Dante.Application.IntegracaoDeCi;
using Dante.Infrastructure.Contextos;
using Dante.Infrastructure.IntegracaoDeCi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure.Composicao;

internal static class IntegracaoDeCiServiceCollectionExtensions
{
    // GitHub Actions é o primeiro provedor da porta de CI (#237, AD-76). Sem conexão em IntegracaoDeCi:Conexoes o
    // adapter existe e responde NaoConfigurada sem rede; nada é contatado na inicialização. O HttpClient não segue
    // redirect (o destino de um log nunca recebe a credencial) e não tem timeout próprio: o tempo limite é o da política,
    // por requisição.
    internal static void AddIntegracaoDeCi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => ConexoesDeCiAutorizadas.DaConfiguracao(configuration));
        services.AddSingleton<IProvedorDeCi>(provider => new ProvedorDeCiGitHubActions(
            provider.GetRequiredService<ConexoesDeCiAutorizadas>(),
            provider.GetRequiredService<RepositoryRegistry>(),
            new ClienteDaApiDoGitHub(
                new HttpClient(new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                }) { Timeout = Timeout.InfiniteTimeSpan },
                PoliticaDeConsultaDeCi.Padrao, TimeProvider.System),
            TimeProvider.System));
    }
}
