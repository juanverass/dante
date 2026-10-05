using Dante.Worker;
using Dante.Worker.Agents;
using Dante.Worker.Brain;
using Dante.Worker.Artifacts;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;
using Dante.Worker.Usage;

var builder = Host.CreateApplicationBuilder(args.Length > 0 && args[0] == "--brain" ? [] : args);
if (args.Length > 0 && args[0] == "--brain")
{
    Environment.ExitCode = await BrainConfiguration.RunAsync(args, builder.Configuration);
    return;
}
BrainConfiguration.Register(builder.Services, builder.Configuration);
builder.Services.AddSingleton<IAgentExecutableResolver, AgentExecutableResolver>();
builder.Services.AddSingleton<IAgentProcessExecutor, AgentProcessExecutor>();
builder.Services.AddSingleton<IInteractiveAgentProcessLauncher, InteractiveAgentProcessLauncher>();
builder.Services.AddSingleton<GeneralWorkspace>();
builder.Services.AddSingleton<ICodexRunner, CodexRunner>();
builder.Services.AddSingleton<IClaudeRunner, ClaudeRunner>();
builder.Services.AddSingleton<IAgentModelCatalog, AgentModelCatalog>();
builder.Services.AddSingleton<IUsageQuotaReader, UsageQuotaReader>();
builder.Services.AddSingleton<JobRegistry>();
builder.Services.AddSingleton<IMediaTools>(_ => new MediaTools());
builder.Services.AddSingleton<MediaPreparer>();
builder.Services.AddSingleton<IAgentSessionDriverFactory, AgentSessionDriverFactory>();
builder.Services.AddSingleton<TelegramDeliveryService>();
builder.Services.AddSingleton<IShowcaseRenderer, ShowcaseRenderer>();
// Showcase turns (#98) are intercepted before the delivery; everything else passes through to it.
builder.Services.AddSingleton<TelegramShowcase>(provider => new TelegramShowcase(
    provider.GetRequiredService<TelegramDeliveryService>(), provider.GetRequiredService<TelegramDeliveryService>(),
    provider.GetRequiredService<IShowcaseRenderer>(), provider.GetRequiredService<AttachmentStore>(),
    provider.GetRequiredService<ILogger<TelegramShowcase>>()));
builder.Services.AddSingleton<IAgentSessionEventSink>(provider => provider.GetRequiredService<TelegramShowcase>());
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<RepositoryRegistry>();
builder.Services.AddSingleton<AssistantSettingsStore>();
builder.Services.AddSingleton(_ => new AttachmentStore());
builder.Services.AddSingleton(_ => new ArtifactStore());
builder.Services.AddSingleton<PendingAttachments>(provider =>
    new PendingAttachments(provider.GetRequiredService<AttachmentStore>()));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.AddSingleton<TelegramUserAuthorizer>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<ITelegramBotApi, TelegramBotApi>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<TelegramPollingService>();

var host = builder.Build();
host.Run();
