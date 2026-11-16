using System.Diagnostics;
using Dante.Application;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure;
using Dante.Worker.Execucoes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.ProcessProbe;

// #300: um "Worker" mínimo com o ProcessadorDaFila real, para o teste matar o processo no meio de uma etapa. A conexão
// vem do ambiente (DANTE_FILA_CONEXAO), nunca dos argumentos. A etapa marca o início sob posse, inicia um processo
// filho (a "CLI") e espera por ele indefinidamente. Nenhuma unidade systemd é listada nem parada.
internal static class ProcessadorDaFilaDoProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var conexao = Environment.GetEnvironmentVariable("DANTE_FILA_CONEXAO");
        if (string.IsNullOrWhiteSpace(conexao)) return 2;
        var services = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = conexao }).Build());
        services.AddSingleton<IUnidadesDeContencao, SemUnidades>();
        var identidade = IdentidadeDoProcessador.DoProcessoAtual();
        await using var provider = services.BuildServiceProvider();
        var processador = new ProcessadorDaFila(provider.GetRequiredService<IServiceScopeFactory>(), identidade,
            new OpcoesDaFila { Lease = TimeSpan.FromSeconds(30), Heartbeat = TimeSpan.FromSeconds(1), Polling = TimeSpan.FromMilliseconds(200) },
            new SinalDaFila(), [new ExecutorQueIniciaUmFilho(args[1])], new MetricasDaFila(),
            NullLogger<ProcessadorDaFila>.Instance);
        Console.WriteLine("boot:" + identidade.Boot);
        await processador.StartAsync(CancellationToken.None);
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private sealed class ExecutorQueIniciaUmFilho(string runtimeConfig) : IExecutorDeEtapa
    {
        public TipoDeExecucao Tipo => TipoDeExecucao.Avulsa;
        public bool PodeExecutar(AlvoDoTrabalhoDto alvo) => true;

        public async Task ExecutarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, CancellationToken cancellationToken)
        {
            await controle.IniciarAsync(cancellationToken);
            using var filho = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { "exec", "--runtimeconfig", runtimeConfig, typeof(Program).Assembly.Location, "wait" },
                UseShellExecute = false,
                RedirectStandardOutput = true
            })!;
            Console.WriteLine($"rodando:{alvo.Codigo}:{filho.Id}");
            await filho.WaitForExitAsync(cancellationToken);
            await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, filho.ExitCode), cancellationToken);
        }
    }

    private sealed class SemUnidades : IUnidadesDeContencao
    {
        public Task<IReadOnlyList<string>> ListarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task PararAsync(string unidade, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("O probe nunca para unidades.");
    }
}
