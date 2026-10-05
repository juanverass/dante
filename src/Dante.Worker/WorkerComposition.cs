using Dante.Worker.Agents;
using Dante.Worker.Artifacts;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Dante.Worker.Usage;
using Microsoft.Extensions.Configuration;

namespace Dante.Worker;

// Apenas composição do legado: migração dos serviços/adapters fica em #166/#167.
internal static class WorkerComposition
{
    public static IServiceCollection AddWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAgentExecutableResolver, AgentExecutableResolver>();
        services.AddSingleton<IAgentProcessExecutor, AgentProcessExecutor>();
        services.AddSingleton<IInteractiveAgentProcessLauncher, InteractiveAgentProcessLauncher>();
        services.AddSingleton<GeneralWorkspace>();
        services.AddSingleton<ICodexRunner, CodexRunner>();
        services.AddSingleton<IClaudeRunner, ClaudeRunner>();
        services.AddSingleton<IAgentModelCatalog, AgentModelCatalog>();
        services.AddSingleton<IUsageQuotaReader, UsageQuotaReader>();
        services.AddSingleton<JobRegistry>();
        services.AddSingleton<IMediaTools>(_ => new MediaTools());
        services.AddSingleton<MediaPreparer>();
        services.AddSingleton<IAgentSessionDriverFactory, AgentSessionDriverFactory>();
        services.AddSingleton<TelegramDeliveryService>();
        services.AddSingleton<IShowcaseRenderer, ShowcaseRenderer>();
        // Showcase turns (#98) are intercepted before the delivery; everything else passes through to it.
        services.AddSingleton<TelegramShowcase>(provider => new TelegramShowcase(
            provider.GetRequiredService<TelegramDeliveryService>(), provider.GetRequiredService<TelegramDeliveryService>(),
            provider.GetRequiredService<IShowcaseRenderer>(), provider.GetRequiredService<AttachmentStore>(),
            provider.GetRequiredService<ILogger<TelegramShowcase>>()));
        services.AddSingleton<IAgentSessionEventSink>(provider => provider.GetRequiredService<TelegramShowcase>());
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<RepositoryRegistry>();
        services.AddSingleton<AssistantSettingsStore>();
        services.AddSingleton(_ => new AttachmentStore());
        services.AddSingleton(_ => new ArtifactStore());
        services.AddSingleton<PendingAttachments>(provider =>
            new PendingAttachments(provider.GetRequiredService<AttachmentStore>()));
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.AddSingleton<TelegramUserAuthorizer>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<ITelegramBotApi, TelegramBotApi>();
        services.AddHostedService<Worker>();
        services.AddHostedService<TelegramPollingService>();
        return services;
    }
}
