using System.Diagnostics;
using System.Threading.Channels;
using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.ProcessProbe;
using Dante.Worker.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Dante.Tests.FilaDuravelPostgreSqlTests;

namespace Dante.Tests;

// #300, ponta a ponta: o Worker morto no meio de uma etapa (Dante.ProcessProbe hospedando o ProcessadorDaFila real) e
// os jobs /claude e /codex pelo caminho da fila com as mesmas respostas do legado.
[Collection("FilaDuravel")]
public sealed class FilaDuravelEndToEndTests
{
    [PostgreSqlFact]
    public async Task MatarOWorkerDuranteAEtapaDeixaEstadoRecuperavelEAuditavelSemRepetirAEscrita()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var (execucao, _) = await CriarAsync(provider, "e2e:escrita", PoliticaDeRetomada.ComConfirmacao, escrita: true);
        using var worker = IniciarWorker(banco);
        int? cli = null;
        try
        {
            var boot = Guid.Parse((await LinhaAsync(worker, "boot:"))["boot:".Length..]);
            var rodando = await LinhaAsync(worker, "rodando:");
            Assert.StartsWith($"rodando:{execucao.Codigo}:", rodando);
            cli = int.Parse(rodando.Split(':')[2]);

            // Worker morto (SIGKILL) com a "CLI" viva: nada de encerramento gracioso.
            worker.Kill(entireProcessTree: false);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            // Estado auditável e recuperável: etapa em execução, trabalho reivindicado pelo boot morto, com lease.
            var depois = await ExecucaoAsync(provider, execucao.Numero);
            Assert.Equal((EstadoDaExecucao.EmExecucao, EstadoDaEtapa.EmExecucao), (depois.Estado, depois.Etapas[0].Estado));
            var trabalho = await TrabalhoAsync(provider, "e2e:escrita");
            Assert.Equal((EstadoDoTrabalho.Reivindicado, boot, 1L), (trabalho.Estado, trabalho.BootDoTrabalhador, trabalho.Geracao));
            Assert.StartsWith(Environment.MachineName + ":", trabalho.Trabalhador);
            Assert.True(trabalho.LeaseAte > DateTimeOffset.UtcNow);

            // Novo boot no mesmo host: a recuperação devolve o trabalho sem esperar os 30 s de lease, e a política
            // ComConfirmacao interrompe a escrita em vez de repeti-la.
            var executor = ExecutorDeTeste.QueConclui();
            var processador = CriarProcessador(provider, IdentidadeDoProcessador.DoProcessoAtual(), Rapidas, executor);
            await processador.StartAsync(CancellationToken.None);
            try
            {
                await Eventually(async () => (await ExecucaoAsync(provider, execucao.Numero)).Estado == EstadoDaExecucao.Interrompida,
                    TimeSpan.FromSeconds(10));
                Assert.Equal(1, processador.UltimaRecuperacao!.TrabalhosDevolvidos);
                await Task.Delay(500);
                Assert.Empty(executor.Execucoes);
                var final = await ExecucaoAsync(provider, execucao.Numero);
                Assert.Equal(EstadoDaEtapa.Interrompida, final.Etapas[0].Estado);
                Assert.Equal(["Pendente→EmExecucao", "Pendente→EmExecucao", "EmExecucao→Interrompida", "EmExecucao→Interrompida"],
                    final.Eventos.Where(e => e.Tipo is TipoDeEventoDeOrquestracao.ExecucaoTransitou or TipoDeEventoDeOrquestracao.EtapaTransitou)
                        .Select(e => Transicao(e.Dados!)));
                var descartado = await TrabalhoAsync(provider, "e2e:escrita");
                Assert.Equal((EstadoDoTrabalho.Descartado, 2L), (descartado.Estado, descartado.Geracao));
                Assert.Equal(EstadoDaExecucao.Interrompida, Assert.Single(executor.Interrupcoes).NovoEstado);
            }
            finally
            {
                await processador.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            // O processo filho órfão é do teste: no serviço, o cgroup do Worker (systemd) ou o scope da AD-63 o encerram.
            if (cli is { } pid)
            {
                try { using var orfao = Process.GetProcessById(pid); orfao.Kill(); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    // Review do PR #304: o Worker morto (SIGKILL) repetidamente no meio da etapa, com o lease vencendo entre um boot e
    // outro (cada boot num host diferente, para a recuperação na inicialização não devolver o trabalho antes). A política
    // Automatica retoma, mas cada reivindicação conta: depois do limite, nenhum boot inicia a etapa de novo.
    [PostgreSqlFact]
    public async Task SigkillsRepetidosComLeaseVencidoNaoUltrapassamOLimiteDeTentativas()
    {
        const int maximo = 3;
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        var (execucao, _) = await CriarAsync(provider, "e2e:esgotado", PoliticaDeRetomada.Automatica, escrita: false);
        await LimitarTentativasAsync(banco, "e2e:esgotado", maximo);
        var filhos = new List<int>();
        try
        {
            for (var boot = 1; boot <= maximo + 2 && !(await TrabalhoAsync(provider, "e2e:esgotado")).Terminal; boot++)
            {
                using var worker = IniciarWorker(banco, $"host-morto-{boot}", leaseMs: 2000);
                try
                {
                    using var parar = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var rodando = LinhaAsync(worker, "rodando:");
                    // Quando o esgotamento vence, a leitura termina com o processo morto: a exceção é esperada.
                    _ = rodando.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                    var terminal = EsperarTerminalAsync(provider, "e2e:esgotado", parar.Token);
                    if (await Task.WhenAny(rodando, terminal) == rodando)
                    {
                        filhos.Add(int.Parse((await rodando).Split(':')[2]));
                        // SIGKILL no meio da etapa: nada de encerramento gracioso nem devolução.
                        worker.Kill(entireProcessTree: false);
                        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        var preso = await TrabalhoAsync(provider, "e2e:esgotado");
                        Assert.Equal((EstadoDoTrabalho.Reivindicado, EstadoDaEtapa.EmExecucao),
                            (preso.Estado, (await ExecucaoAsync(provider, execucao.Numero)).Etapas[0].Estado));
                        Assert.True(preso.LeaseAte > DateTimeOffset.UtcNow);
                    }
                    else await terminal;
                    await parar.CancelAsync();
                }
                finally
                {
                    if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                }
            }

            // Tentativa 1 executa; a 2 retoma a etapa (Automatica) e a 3 executa de novo; a 4 está além do limite.
            Assert.Equal(2, filhos.Count);
            var trabalho = await TrabalhoAsync(provider, "e2e:esgotado");
            Assert.Equal((EstadoDoTrabalho.Falhou, maximo + 1, (long)maximo + 1), (trabalho.Estado, trabalho.Tentativas, trabalho.Geracao));
            var final = await ExecucaoAsync(provider, execucao.Numero);
            Assert.Equal((EstadoDaExecucao.Falhou, EstadoDaEtapa.Falhou, 2), (final.Estado, final.Etapas[0].Estado, final.Etapas[0].Tentativa));
            Assert.StartsWith("Tentativas esgotadas", final.Erro);

            // Outro processador vivo depois do esgotamento: nada a reivindicar e nenhum executor iniciado.
            var executor = ExecutorDeTeste.QueConclui();
            var processador = CriarProcessador(provider, Processador(), Rapidas, executor);
            await processador.StartAsync(CancellationToken.None);
            await Task.Delay(1000);
            await processador.StopAsync(CancellationToken.None);
            Assert.Empty(executor.Execucoes);
            Assert.Equal(EstadoDaExecucao.Falhou, (await ExecucaoAsync(provider, execucao.Numero)).Estado);
        }
        finally
        {
            foreach (var pid in filhos)
            {
                try { using var orfao = Process.GetProcessById(pid); orfao.Kill(); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    [PostgreSqlFact]
    public async Task JobsPelaFilaPreservamAsRespostasDoLegadoEGravamOLedger()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var espelho = new EspelhoDoLedger(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EspelhoDoLedger>.Instance);
        var jobs = new JobRegistry(espelho);
        var sinal = new SinalDaFila();
        var metricas = new MetricasDaFila();
        var api = new BotDeTeste();
        var executor = new ExecutorDeJobsDaFila(provider.GetRequiredService<IServiceScopeFactory>(), jobs, espelho, sinal,
            metricas, NullLogger<ExecutorDeJobsDaFila>.Instance, api);
        var processador = new ProcessadorDaFila(provider.GetRequiredService<IServiceScopeFactory>(),
            IdentidadeDoProcessador.DoProcessoAtual(), Rapidas with { Polling = TimeSpan.FromSeconds(5) }, sinal, [executor],
            metricas, NullLogger<ProcessadorDaFila>.Instance);
        var runner = new RunnerDeTeste();
        var options = Options.Create(new TelegramOptions
        {
            BotToken = "test-token",
            AllowedUserIds = "123",
            AgentWorkingDirectory = "/tmp/agent-work"
        });
        using var service = new TelegramPollingService(api, options, new TelegramUserAuthorizer(options), runner, runner,
            jobs, NullLogger<TelegramPollingService>.Instance, filaDeJobs: executor);
        await processador.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        try
        {
            api.Enviar("/codex responda", 1);
            var iniciado = await api.ProximaAsync();
            Assert.Matches(@"^Codex iniciado\. Job ID: J\d{6} \(General\)", iniciado);
            var id = iniciado.Split("Job ID: ")[1][..7];
            Assert.Equal($"Codex concluído. Job {id} (General).\n\ndone", await api.ProximaAsync());
            api.Enviar("/status", 2);
            Assert.Contains($"{id} Codex General: Succeeded", await api.ProximaAsync());
            var execucao = await ExecucaoAsync(provider, long.Parse(id[1..]));
            Assert.Equal((EstadoDaExecucao.Concluida, EstadoDaEtapa.Concluida, 0), (execucao.Estado, execucao.Etapas[0].Estado, execucao.Etapas[0].CodigoDeSaida));
            Assert.Equal(EstadoDoTrabalho.Concluido, (await TrabalhoAsync(provider, "telegram:-123:1")).Estado);
            Assert.Null(execucao.Objetivo);

            // A mesma mensagem do Telegram entregue de novo não cria outra execução.
            api.Enviar("/codex responda", 1);
            Assert.Equal($"Pedido já recebido como {id}.", await api.ProximaAsync());

            // /cancel continua valendo para o job da fila; o ledger registra o cancelamento.
            runner.Bloquear = true;
            api.Enviar("/claude espere", 3);
            var bloqueado = (await api.ProximaAsync()).Split("Job ID: ")[1][..7];
            await runner.Iniciado.Task.WaitAsync(TimeSpan.FromSeconds(10));
            api.Enviar($"/cancel {bloqueado}", 4);
            var respostas = new[] { await api.ProximaAsync(), await api.ProximaAsync() };
            Assert.Contains(respostas, r => r.StartsWith($"Cancelamento solicitado para {bloqueado}"));
            Assert.Contains(respostas, r => r.StartsWith($"Claude cancelado. Job {bloqueado}"));
            await Eventually(async () => (await ExecucaoAsync(provider, long.Parse(bloqueado[1..]))).Estado == EstadoDaExecucao.Cancelada);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await processador.StopAsync(CancellationToken.None);
        }
    }

    [PostgreSqlFact]
    public async Task JobQueNaoIniciouAntesDoReinicioECanceladoComAvisoENuncaExecutado()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        // O job foi enfileirado por um boot que morreu antes de reivindicá-lo: o pedido só existia naquela memória.
        var job = await EnfileirarJobAsync(provider, "telegram:-123:77");
        await using var espelho = new EspelhoDoLedger(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EspelhoDoLedger>.Instance);
        var api = new BotDeTeste();
        var sinal = new SinalDaFila();
        var executor = new ExecutorDeJobsDaFila(provider.GetRequiredService<IServiceScopeFactory>(), new JobRegistry(espelho),
            espelho, sinal, new MetricasDaFila(), NullLogger<ExecutorDeJobsDaFila>.Instance, api) ;
        var processador = new ProcessadorDaFila(provider.GetRequiredService<IServiceScopeFactory>(),
            IdentidadeDoProcessador.DoProcessoAtual(), Rapidas, sinal, [executor], new MetricasDaFila(),
            NullLogger<ProcessadorDaFila>.Instance);
        await processador.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal($"J{job.Numero:D6} não chegou a iniciar antes do reinício do Worker e foi cancelado: o pedido do job não é guardado. Reenvie se ainda precisar.",
                await api.ProximaAsync(chat: 1001));
            var execucao = await ExecucaoAsync(provider, job.Numero);
            Assert.Equal((EstadoDaExecucao.Cancelada, EstadoDaEtapa.Cancelada), (execucao.Estado, execucao.Etapas[0].Estado));
            Assert.Equal(EstadoDoTrabalho.Descartado, (await TrabalhoAsync(provider, "telegram:-123:77")).Estado);
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }
    }

    // Review do PR #304: job deste boot cujas tentativas se esgotaram (aqui, por um processo morto que o reivindicou) não
    // é executado; termina Failed no registry, em vez de ficar Queued no /status, e o dono é avisado.
    [PostgreSqlFact]
    public async Task JobQueEsgotouAsTentativasNaoExecutaEFalhaComAviso()
    {
        await using var banco = await FilaAsync();
        await using var provider = Provider(banco.ConnectionString);
        await using var espelho = new EspelhoDoLedger(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EspelhoDoLedger>.Instance);
        var jobs = new JobRegistry(espelho);
        var api = new BotDeTeste();
        var sinal = new SinalDaFila();
        var executor = new ExecutorDeJobsDaFila(provider.GetRequiredService<IServiceScopeFactory>(), jobs, espelho, sinal,
            new MetricasDaFila(), NullLogger<ExecutorDeJobsDaFila>.Instance, api);
        var criado = await executor.CriarAsync("codex", JobExecutionContext.General("/g"), 1001, "telegram:-123:90", null,
            CancellationToken.None);
        var job = criado.Job!;
        var executado = false;
        executor.Liberar(job.Id, _ => { executado = true; return Task.CompletedTask; });
        await LimitarTentativasAsync(banco, "telegram:-123:90", 1);
        Assert.NotNull(await ReivindicarAsync(provider, Processador("host-morto"), TimeSpan.FromMilliseconds(200)));
        await Task.Delay(300);

        var processador = new ProcessadorDaFila(provider.GetRequiredService<IServiceScopeFactory>(),
            IdentidadeDoProcessador.DoProcessoAtual(), Rapidas, sinal, [executor], new MetricasDaFila(),
            NullLogger<ProcessadorDaFila>.Instance);
        await processador.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal($"{job.Id} falhou: esgotou as tentativas de execução da fila e não será repetido automaticamente.",
                await api.ProximaAsync(chat: 1001));
        }
        finally
        {
            await processador.StopAsync(CancellationToken.None);
        }
        Assert.False(executado);
        Assert.Equal(JobStatus.Failed, jobs.GetVisible().Single(j => j.Id == job.Id).Status);
        var execucao = await ExecucaoAsync(provider, long.Parse(job.Id[1..]));
        Assert.Equal((EstadoDaExecucao.Falhou, EstadoDaEtapa.Falhou), (execucao.Estado, execucao.Etapas[0].Estado));
        Assert.Equal(EstadoDoTrabalho.Falhou, (await TrabalhoAsync(provider, "telegram:-123:90")).Estado);
    }

    // Dante.ProcessProbe hospedando o ProcessadorDaFila real. A conexão (com senha) vai pelo ambiente do filho, nunca
    // pela linha de comando.
    private static Process IniciarWorker(Banco banco, string? host = null, int? leaseMs = null)
    {
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
        var inicio = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/bin/dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "exec", "--runtimeconfig", runtimeConfig, typeof(ProbeMarker).Assembly.Location,
                     "processador-da-fila", runtimeConfig })
            inicio.ArgumentList.Add(arg);
        inicio.Environment["DANTE_FILA_CONEXAO"] = banco.ConnectionString;
        if (host is not null) inicio.Environment["DANTE_FILA_HOST"] = host;
        if (leaseMs is { } ms) inicio.Environment["DANTE_FILA_LEASE_MS"] = ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Process.Start(inicio)!;
    }

    private static async Task EsperarTerminalAsync(IServiceProvider provider, string chave, CancellationToken cancellationToken)
    {
        while (!(await TrabalhoAsync(provider, chave)).Terminal) await Task.Delay(100, cancellationToken);
    }

    private static string Transicao(string dados)
    {
        using var json = System.Text.Json.JsonDocument.Parse(dados);
        return json.RootElement.GetProperty("de").GetString() + "→" + json.RootElement.GetProperty("para").GetString();
    }

    private static async Task<string> LinhaAsync(Process processo, string prefixo)
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (await processo.StandardOutput.ReadLineAsync(prazo.Token) is { } linha)
            if (linha.StartsWith(prefixo, StringComparison.Ordinal)) return linha;
        throw new InvalidOperationException("O processo terminou sem a linha " + prefixo + ": " +
            await processo.StandardError.ReadToEndAsync());
    }

    private sealed class BotDeTeste : ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates = Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<(long Chat, string Texto)> mensagens = Channel.CreateUnbounded<(long, string)>();
        private long proximo;

        public void Enviar(string texto, long idDaMensagem) => updates.Writer.TryWrite(new TelegramUpdate(
            Interlocked.Increment(ref proximo),
            new TelegramMessage(new TelegramChat(-123), texto, new TelegramUser(123), idDaMensagem)));

        public async Task<string> ProximaAsync(long chat = -123)
        {
            var (destino, texto) = await mensagens.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(chat, destino);
            return texto;
        }

        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
            [await updates.Reader.ReadAsync(cancellationToken)];

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            mensagens.Writer.TryWrite((chatId, text));
            return Task.CompletedTask;
        }
    }

    private sealed class RunnerDeTeste : ICodexRunner, IClaudeRunner
    {
        public bool Bloquear { get; set; }
        public TaskCompletionSource Iniciado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentProcessResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default, bool generalMode = false,
            IReadOnlyDictionary<string, string>? environment = null, string? model = null, string? effort = null,
            IReadOnlyList<Dante.Application.Anexos.Attachment>? attachments = null, IReadOnlyList<string>? additionalDirectories = null)
        {
            if (Bloquear)
            {
                Iniciado.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new AgentProcessResult(AgentProcessStatus.Succeeded, "done", "", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
    }
}
