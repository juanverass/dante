using Dante.Worker.Artifacts;
using Dante.Worker.Brain;
using Dante.Worker.Execucoes;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Planilhas;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Configuration;

namespace Dante.Worker;

// Composição do legado que ainda vive no Worker; adapters já migrados são compostos por AddInfrastructure (#167).
internal static class WorkerComposition
{
    public static IServiceCollection AddWorker(this IServiceCollection services, IConfiguration configuration)
    {
        // Ledger de execuções (AD-66): exige banco e a chave própria; a connection string sozinha não o liga. Sem o
        // espelho, JobRegistry e SessionRegistry seguem só em memória, como antes.
        if (OrquestracaoHabilitada(configuration)) services.AddLedgerDeExecucoes();
        services.AddSingleton<JobRegistry>();
        services.AddSingleton<RegistroDeOperacoesBrain>();
        services.AddSingleton(provider => new TelegramBrain(provider.GetRequiredService<IServiceScopeFactory>(), configuration, provider.GetRequiredService<RegistroDeOperacoesBrain>()));
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
        // Brain metrics (#148) observe the session events on their way to the showcase and the delivery.
        services.AddSingleton<IAgentSessionEventSink>(provider => new MetricasDeSessaoDoBrain(
            provider.GetRequiredService<TelegramShowcase>(), provider.GetRequiredService<TelegramBrain>()));
        services.AddSingleton<OperacoesMcpDoBrain>();
        services.AddSingleton<IProvedorDeFerramentas, FerramentasDePlanilhaParaAgentes>();
        services.AddSingleton<IProvedorDeFerramentas, FerramentasDoBrainParaAgentes>();
        services.AddSingleton<IAgentToolServers, CompositorDeFerramentas>();
        services.AddSingleton<TelegramPlanilhas>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton(_ => new AttachmentStore());
        services.AddSingleton(_ => new ArtifactStore());
        services.AddSingleton<PendingAttachments>(provider =>
            new PendingAttachments(provider.GetRequiredService<AttachmentStore>()));
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.AddSingleton<TelegramUserAuthorizer>();
        // Only the Telegram adapter consumes this client. Same transport and 100s timeout as `new HttpClient()`, but
        // connecting with Happy Eyeballs (IPv6 that ignores the SYN no longer blocks IPv4) and connection diagnostics.
        services.AddSingleton(provider => new HttpClient(DiagnosticoHttpDoTelegram.CriarHandler(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DiagnosticoHttpDoTelegram)))));
        services.AddSingleton<ITelegramBotApi, TelegramBotApi>();
        services.AddHostedService<Worker>();
        services.AddHostedService<TelegramPollingService>();
        return services;
    }

    // Antes dos demais hosted services: o encerramento do ledger para por último (ordem inversa).
    internal static IServiceCollection AddLedgerDeExecucoes(this IServiceCollection services)
    {
        services.AddSingleton<EspelhoDoLedger>();
        services.AddHostedService<EncerramentoDoLedger>();
        return services;
    }

    internal static bool OrquestracaoHabilitada(IConfiguration configuration) =>
        bool.TryParse(configuration["Orquestracao:Habilitada"], out var habilitada) && habilitada &&
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Dante"));
}
