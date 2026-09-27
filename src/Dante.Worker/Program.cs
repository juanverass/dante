using Dante.Worker;
using Dante.Worker.Agents;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IAgentExecutableResolver, AgentExecutableResolver>();
builder.Services.AddSingleton<IAgentProcessExecutor, AgentProcessExecutor>();
builder.Services.AddSingleton<ICodexRunner, CodexRunner>();
builder.Services.AddSingleton<IClaudeRunner, ClaudeRunner>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
