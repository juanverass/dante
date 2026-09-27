using Dante.Worker;
using Dante.Worker.Agents;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IAgentExecutableResolver, AgentExecutableResolver>();
builder.Services.AddSingleton<IAgentProcessExecutor, AgentProcessExecutor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
