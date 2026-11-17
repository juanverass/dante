using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Modulos.Agentes;
using Dante.Infrastructure.Modulos.Execucoes;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure.Composicao;

internal static class OrquestracaoServiceCollectionExtensions
{
    // Registrado sempre que há banco, como o Brain: a migration precisa existir antes de o host ligar o ledger pela
    // chave Orquestracao:Habilitada. Registrar não conecta nem migra.
    internal static void AddOrquestracao(this IServiceCollection services, string connectionString)
    {
        // Sem AddDbContext: um segundo AddDbContext duplicaria o registro não genérico de DbContextOptions do Brain.
        var opcoes = OrquestracaoDbContextFactory.Opcoes(connectionString);
        services.AddScoped(_ => new OrquestracaoDbContext(opcoes));
        services.AddScoped<IExecucaoRepository, ExecucaoRepository>();
        services.AddScoped<IEtapaDaExecucaoRepository, EtapaDaExecucaoRepository>();
        services.AddScoped<IDependenciaDeEtapaRepository, DependenciaDeEtapaRepository>();
        services.AddScoped<ISessaoDeAgenteRepository, SessaoDeAgenteRepository>();
        services.AddScoped<IEventoDeOrquestracaoRepository, EventoDeOrquestracaoRepository>();
        services.AddScoped<IReservaDeWorktreeRepository, ReservaDeWorktreeRepository>();
        services.AddScoped<IUnitOfWorkDaOrquestracao, UnitOfWorkDaOrquestracao>();
        // Cadastro de agentes (AD-68): mesmo contexto e mesma unidade de trabalho do ledger.
        services.AddScoped<IAgenteRepository, AgenteRepository>();
        services.AddScoped<IVersaoDoAgenteRepository, VersaoDoAgenteRepository>();
    }
}
