using Dante.Application.Contextos;
using Dante.Infrastructure.Contextos;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Infrastructure.Composicao;

internal static class ContextServiceCollectionExtensions
{
    // Portas e adapters legados compartilham a instância usada também pela API de escrita do Telegram.
    internal static void AddContextos(this IServiceCollection services)
    {
        services.AddSingleton<GeneralWorkspace>();
        services.AddSingleton<IWorkspaceGeral>(provider => provider.GetRequiredService<GeneralWorkspace>());
        services.AddSingleton<RepositoryRegistry>();
        services.AddSingleton<ICatalogoDeRepositorios>(provider => provider.GetRequiredService<RepositoryRegistry>());
        services.AddSingleton<AssistantSettingsStore>();
        services.AddSingleton<IPreferenciasDoAssistente>(provider =>
            provider.GetRequiredService<AssistantSettingsStore>());
    }
}
