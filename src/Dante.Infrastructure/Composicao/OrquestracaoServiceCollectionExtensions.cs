using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Application.Grupos;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Modulos.Agentes;
using Dante.Infrastructure.Modulos.Execucoes;
using Dante.Infrastructure.Modulos.Grupos;
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
        // Roteiro de grupo (#308): snapshot, participantes congelados e gate humano.
        services.AddScoped<IRoteiroDaExecucaoRepository, RoteiroDaExecucaoRepository>();
        services.AddScoped<IParticipanteDaExecucaoRepository, ParticipanteDaExecucaoRepository>();
        services.AddScoped<IPedidoDeAprovacaoRepository, PedidoDeAprovacaoRepository>();
        services.AddScoped<IVereditoRegistradoRepository, VereditoRegistradoRepository>();
        // Delegações rastreadas (AD-70, #309).
        services.AddScoped<IDelegacaoRepository, DelegacaoRepository>();
        // Coordenação por papel (AD-75, #310): snapshot e revisões do plano.
        services.AddScoped<ICoordenacaoDaExecucaoRepository, CoordenacaoDaExecucaoRepository>();
        services.AddScoped<IPlanoDaCoordenacaoRepository, PlanoDaCoordenacaoRepository>();
        services.AddScoped<ISinteseDaCoordenacaoRepository, SinteseDaCoordenacaoRepository>();
        services.AddScoped<IUnitOfWorkDaOrquestracao, UnitOfWorkDaOrquestracao>();
        // Cadastro de agentes (AD-68): mesmo contexto e mesma unidade de trabalho do ledger.
        services.AddScoped<IAgenteRepository, AgenteRepository>();
        services.AddScoped<IVersaoDoAgenteRepository, VersaoDoAgenteRepository>();
        // Grupos de agentes (AD-69): idem.
        services.AddScoped<IGrupoDeAgentesRepository, GrupoDeAgentesRepository>();
        services.AddScoped<IVersaoDoGrupoRepository, VersaoDoGrupoRepository>();
        services.AddScoped<IAtivacaoDeVersaoDoGrupoRepository, AtivacaoDeVersaoDoGrupoRepository>();
    }
}
