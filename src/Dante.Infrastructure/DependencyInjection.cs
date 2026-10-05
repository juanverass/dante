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
        // Adapters e persistência serão registrados pelas respectivas issues.
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
