using Dante.Application.Execucoes;
using Dante.Infrastructure.Agentes;
using Dante.Infrastructure.Fila;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Dante.Infrastructure.Composicao;

internal static class FilaServiceCollectionExtensions
{
    // Fila durável (AD-67): registrada com o ledger, sempre que há banco. Registrar não conecta, não escuta nem migra;
    // o Worker só a usa com Orquestracao:Habilitada.
    internal static void AddFilaDuravel(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IFilaDeTrabalho, FilaDeTrabalhoPostgreSql>();
        services.AddSingleton<IOuvinteDaFila>(provider => new OuvinteDaFilaPostgreSql(connectionString,
            provider.GetService<ILoggerFactory>()?.CreateLogger<OuvinteDaFilaPostgreSql>()));
        services.AddSingleton<IUnidadesDeContencao, UnidadesDeContencaoSystemd>();
    }
}
