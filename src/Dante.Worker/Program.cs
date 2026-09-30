using Dante.Worker;
using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using Dante.Worker.Settings;
using Dante.Worker.Telegram;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IAgentExecutableResolver, AgentExecutableResolver>();
builder.Services.AddSingleton<IAgentProcessExecutor, AgentProcessExecutor>();
builder.Services.AddSingleton<IInteractiveAgentProcessLauncher, InteractiveAgentProcessLauncher>();
builder.Services.AddSingleton<GeneralWorkspace>();
builder.Services.AddSingleton<ICodexRunner, CodexRunner>();
builder.Services.AddSingleton<IClaudeRunner, ClaudeRunner>();
builder.Services.AddSingleton<JobRegistry>();
builder.Services.AddSingleton<RepositoryRegistry>();
builder.Services.AddSingleton<AssistantSettingsStore>();
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.AddSingleton<TelegramUserAuthorizer>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<ITelegramBotApi, TelegramBotApi>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<TelegramPollingService>();

var host = builder.Build();
host.Run();
