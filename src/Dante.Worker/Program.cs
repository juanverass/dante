using Dante.Infrastructure.Brain;
using Dante.Application;
using Dante.Infrastructure;
using Dante.Worker;
using Dante.Worker.Planilhas;
using Dante.Worker.Brain;

if (args.FirstOrDefault() == ServidorMcpDoBrain.Argumento)
{
    Environment.ExitCode = await ServidorMcpDoBrain.ExecutarProcessoAsync(args);
    return;
}

// Servidor MCP de planilhas iniciado pela CLI do agente numa sessão (#224): sem Telegram nem host.
if (args.FirstOrDefault() == ServidorMcpDePlanilhas.Argumento)
{
    Environment.ExitCode = await ServidorMcpDePlanilhas.ExecutarProcessoAsync(args);
    return;
}

var builder = Host.CreateApplicationBuilder(args.FirstOrDefault() == "--brain" ? [] : args);
builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddWorker(builder.Configuration);
var host = builder.Build();
if (args.FirstOrDefault() == "--brain")
{
    Environment.ExitCode = await ComandosDoBrain.ExecutarAsync(host.Services, args);
    return;
}
host.Run();
