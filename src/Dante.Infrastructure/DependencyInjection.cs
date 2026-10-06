using Dante.Application.Comum;
using Dante.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Uso;
using Dante.Infrastructure.Agentes;
using Dante.Infrastructure.Contextos;
using Dante.Infrastructure.Uso;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        // Sem configuração, os hosts continuam operando sem banco.
        var connectionString = configuration.GetConnectionString("Dante");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddScoped<Dante.Application.SegurancaDoBrain.AutorizacaoDoBrain>();
            services.AddSingleton(_ => new Dante.Infrastructure.SegurancaDoBrain.IdentidadeTelegramDoBrain(configuration));
            services.AddDbContext<DanteDbContext>(options => options.UseNpgsql(connectionString,
                provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", "brain_meta")));
            services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
            services.AddScoped<IUnitOfWork, UnitOfWork>();
            services.AddScoped<Dante.Application.EspacosDeConhecimento.IEspacoDeConhecimentoRepository, EspacoDeConhecimentoRepository>();
            services.AddScoped<Dante.Application.Projetos.IProjetoRepository, ProjetoRepository>();
            services.AddScoped<Dante.Application.Conhecimentos.IConhecimentoRepository, ConhecimentoRepository>();
            services.AddScoped<AdministracaoDoBanco>();
            services.AddScoped<Dante.Application.QualidadeDoBrain.IConsultaDeQualidade, ConsultaDeQualidadePostgreSql>();
            services.AddScoped<Dante.Application.QualidadeDoBrain.ManutencaoDoBrainAppService>();
            services.AddScoped<Dante.Application.BuscaDoBrain.IIndiceDeBusca, Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>();
            services.AddScoped<Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql>();
            services.AddScoped<Dante.Application.BuscaDoBrain.BuscaDoBrainAppService>();
            services.AddSingleton<Dante.Application.BuscaDoBrain.IGeradorDeEmbedding>(_ => new Dante.Infrastructure.BuscaDoBrain.GeradorDeEmbeddingHttp(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), configuration));
            services.AddScoped<Dante.Application.ContextosDeTrabalho.IContextoDeTrabalhoRepository, ContextoDeTrabalhoRepository>();
            services.AddScoped<Dante.Application.ContextosDeTrabalho.ContextoDeTrabalhoAppService>();
            services.AddScoped<Dante.Application.SegurancaDoBrain.LeituraDoBrainAppService>();
            services.AddScoped<Dante.Application.CapturaDeConhecimento.ICandidatoDeConhecimentoRepository, CandidatoDeConhecimentoRepository>();
            services.AddScoped<Dante.Application.CapturaDeConhecimento.ICapturaDeConhecimentoAppService, Dante.Application.CapturaDeConhecimento.CapturaDeConhecimentoAppService>();
            services.AddScoped<Dante.Application.RelacoesDeConhecimento.IRelacaoDeConhecimentoRepository, RelacaoDeConhecimentoRepository>();
            services.AddScoped<Dante.Application.RelacoesDeConhecimento.IRelacaoDeConhecimentoAppService, Dante.Application.RelacoesDeConhecimento.RelacaoDeConhecimentoAppService>();
            services.AddScoped<Dante.Application.EspacosDeConhecimento.IEspacoDeConhecimentoAppService, Dante.Application.EspacosDeConhecimento.EspacoDeConhecimentoAppService>();
            services.AddScoped<Dante.Application.Projetos.IProjetoAppService, Dante.Application.Projetos.ProjetoAppService>();
            services.AddScoped<Dante.Application.Conhecimentos.IConhecimentoAppService, Dante.Application.Conhecimentos.ConhecimentoAppService>();
        }
        AddContextos(services);
        AddAgentes(services);
        return services;
    }

    // #167: adapters locais das portas de contexto. A porta e o tipo concreto resolvem a mesma instância, porque o
    // Telegram ainda usa a API de escrita dos adapters legados.
    private static void AddContextos(IServiceCollection services)
    {
        services.AddSingleton<GeneralWorkspace>();
        services.AddSingleton<IWorkspaceGeral>(provider => provider.GetRequiredService<GeneralWorkspace>());
        services.AddSingleton<RepositoryRegistry>();
        services.AddSingleton<ICatalogoDeRepositorios>(provider => provider.GetRequiredService<RepositoryRegistry>());
        services.AddSingleton<AssistantSettingsStore>();
        services.AddSingleton<IPreferenciasDoAssistente>(provider =>
            provider.GetRequiredService<AssistantSettingsStore>());
    }

    // #167: execução das CLIs do Claude e do Codex (one-shot, processo interativo, catálogo de modelos e cotas).
    private static void AddAgentes(IServiceCollection services)
    {
        services.AddSingleton<IAgentExecutableResolver, AgentExecutableResolver>();
        services.AddSingleton<IAgentProcessExecutor, AgentProcessExecutor>();
        services.AddSingleton<IInteractiveAgentProcessLauncher, InteractiveAgentProcessLauncher>();
        services.AddSingleton<ICodexRunner, CodexRunner>();
        services.AddSingleton<IClaudeRunner, ClaudeRunner>();
        services.AddSingleton<IAgentModelCatalog, AgentModelCatalog>();
        services.AddSingleton<IUsageQuotaReader, UsageQuotaReader>();
    }
}
