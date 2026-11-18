using Dante.Infrastructure.Banco;
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

// dante_coordenacao (#308): iniciado pela CLI na sessão de revisão de um roteiro; sem Telegram nem host.
if (args.FirstOrDefault() == Dante.Worker.Coordenacao.ServidorMcpDeCoordenacao.Argumento)
{
    Environment.ExitCode = await Dante.Worker.Coordenacao.ServidorMcpDeCoordenacao.ExecutarProcessoAsync(args);
    return;
}

// Servidor MCP de planilhas iniciado pela CLI do agente numa sessão (#224): sem Telegram nem host.
if (args.FirstOrDefault() == ServidorMcpDePlanilhas.Argumento)
{
    Environment.ExitCode = await ServidorMcpDePlanilhas.ExecutarProcessoAsync(args);
    return;
}

// Comandos administrativos não repassam argumentos à configuração do host.
var administrativo = args.FirstOrDefault() is "--brain" or ComandosDoBanco.Orquestracao;
var builder = Host.CreateApplicationBuilder(administrativo ? [] : args);
builder.Services.AddApplication().AddInfrastructure(builder.Configuration).AddWorker(builder.Configuration);
var host = builder.Build();
if (args.FirstOrDefault() == "--brain")
{
    Environment.ExitCode = await ComandosDoBrain.ExecutarAsync(host.Services, args);
    return;
}
// Ledger de execuções (AD-66): migration só por comando explícito, nunca na inicialização.
if (args.FirstOrDefault() == ComandosDoBanco.Orquestracao)
{
    Environment.ExitCode = await ComandosDoBanco.ExecutarAsync(host.Services, args);
    return;
}
host.Run();
