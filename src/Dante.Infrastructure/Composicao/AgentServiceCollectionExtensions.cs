using Dante.Application.Agentes;
using Dante.Application.Uso;
using Dante.Infrastructure.Agentes;
using Dante.Infrastructure.Uso;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure.Composicao;

internal static class AgentServiceCollectionExtensions
{
    internal static void AddAgentes(this IServiceCollection services)
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
