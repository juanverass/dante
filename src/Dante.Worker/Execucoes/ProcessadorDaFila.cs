using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;

namespace Dante.Worker.Execucoes;

// Processador da fila durável (AD-67). Antes de reivindicar qualquer trabalho roda a recuperação na inicialização;
// depois, com até Slots trabalhos ao mesmo tempo, reivindica (FOR UPDATE SKIP LOCKED, lease, geração), avalia,
// executa fora de transação com heartbeat e grava o resultado sob posse. Acorda por sinal em memória, por LISTEN do
// banco ou pelo polling de segurança; sem trabalho disponível, dorme até o próximo disponivel_em, sem busy loop.
// Só existe com Orquestracao:Habilitada (sem a chave, o Worker compõe exatamente o legado).
public sealed class ProcessadorDaFila(
    IServiceScopeFactory escopos,
    IdentidadeDoProcessador identidade,
    OpcoesDaFila opcoes,
    SinalDaFila sinal,
    IEnumerable<IExecutorDeEtapa> executores,
    MetricasDaFila metricas,
    ILogger<ProcessadorDaFila> logger,
    IOuvinteDaFila? ouvinte = null,
    TimeProvider? relogio = null) : BackgroundService
{
    // Espera mínima entre voltas: um trabalho visível mas travado por outro processador não vira busy loop.
    private static readonly TimeSpan EsperaMinima = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan EsperaMaximaAposFalha = TimeSpan.FromSeconds(30);
    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;
    private readonly OpcoesDaFila opcoes = opcoes;
    private readonly MetricasDaFila metricas = metricas;
    private readonly ILogger<ProcessadorDaFila> logger = logger;
    private readonly object gate = new();
    private readonly HashSet<Task> emAndamento = [];
    private readonly TaskCompletionSource recuperacao = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationToken encerramento;

    // Concluída quando a recuperação na inicialização terminou (antes da primeira reivindicação).
    public Task Recuperacao => recuperacao.Task;
    public RelatorioDeRecuperacaoDto? UltimaRecuperacao { get; private set; }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Task[] pendentes;
        lock (gate) pendentes = [.. emAndamento];
        try
        {
            // As etapas em andamento recebem o cancelamento do encerramento e gravam o próprio fim sob posse.
            await Task.WhenAll(pendentes).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Prazo de encerramento esgotado com {Pendentes} trabalho(s) da fila em andamento; a recuperação os trata no próximo boot.",
                pendentes.Count(t => !t.IsCompleted));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        encerramento = stoppingToken;
        opcoes.Validar();
        await RecuperarAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;
        var escuta = ouvinte is null ? Task.CompletedTask
            : Task.Run(() => ouvinte.OuvirAsync(sinal.Sinalizar, stoppingToken), CancellationToken.None);
        var falhasSeguidas = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var espera = opcoes.Polling;
            try
            {
                while (Ocupados < opcoes.Slots && !stoppingToken.IsCancellationRequested)
                {
                    var trabalho = await EmEscopoAsync(s => s.GetRequiredService<IFilaDeTrabalho>()
                        .ReivindicarAsync(identidade, opcoes.Lease, stoppingToken));
                    if (trabalho is null) break;
                    Acompanhar(ProcessarAsync(trabalho));
                }
                falhasSeguidas = 0;
                if (Ocupados < opcoes.Slots &&
                    await EmEscopoAsync(s => s.GetRequiredService<IFilaDeTrabalho>().ProximaDisponibilidadeAsync(stoppingToken)) is { } proxima)
                {
                    var ate = proxima - relogio.GetUtcNow();
                    if (ate < espera) espera = ate < EsperaMinima ? EsperaMinima : ate;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Banco indisponível: espera crescente, sem martelar o servidor nem derrubar o Worker.
                falhasSeguidas++;
                espera = TrabalhoDaFila.Backoff(falhasSeguidas, TimeSpan.FromSeconds(1), EsperaMaximaAposFalha);
                logger.LogWarning("Fila indisponível ao reivindicar ({ErrorType}); nova tentativa em {EsperaSegundos}s.",
                    exception.GetType().Name, espera.TotalSeconds);
            }
            try
            {
                await sinal.EsperarAsync(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        try
        {
            await escuta;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("LISTEN da fila terminou com falha ({ErrorType}).", exception.GetType().Name);
        }
    }

    private int Ocupados
    {
        get { lock (gate) return emAndamento.Count; }
    }

    private void Acompanhar(Task tarefa)
    {
        lock (gate) emAndamento.Add(tarefa);
        _ = tarefa.ContinueWith(concluida =>
        {
            lock (gate) emAndamento.Remove(concluida);
            sinal.Sinalizar();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RecuperarAsync(CancellationToken stoppingToken)
    {
        var espera = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var relatorio = await EmEscopoAsync(s => s.GetRequiredService<IRecuperacaoDeExecucoesAppService>()
                    .RecuperarAsync(identidade, stoppingToken));
                UltimaRecuperacao = relatorio;
                metricas.UnidadesParadas.Add(relatorio.UnidadesParadas.Count);
                metricas.UnidadesDesconhecidas.Add(relatorio.UnidadesDesconhecidas.Count);
                logger.LogInformation(
                    "Recuperação da fila: {UnidadesParadas} unidade(s) de boot anterior parada(s), {UnidadesDesconhecidas} desconhecida(s) relatada(s) sem parar, {FalhasAoParar} falha(s) ao parar, listagem indisponível: {ListagemIndisponivel}; {SessoesInterrompidas} sessão(ões) interrompida(s); {TrabalhosDevolvidos} trabalho(s) de boot anterior devolvido(s). Boot {Boot}.",
                    relatorio.UnidadesParadas.Count, relatorio.UnidadesDesconhecidas.Count, relatorio.FalhasAoParar.Count,
                    relatorio.ListagemIndisponivel, relatorio.SessoesInterrompidas, relatorio.TrabalhosDevolvidos,
                    identidade.Boot);
                foreach (var unidade in relatorio.UnidadesDesconhecidas)
                    logger.LogWarning("Unidade {Unidade} não registrada no ledger como de um boot anterior: relatada, não parada.",
                        unidade);
                foreach (var unidade in relatorio.FalhasAoParar)
                    logger.LogWarning("Falha ao parar a unidade {Unidade} de um boot anterior.", unidade);
                recuperacao.TrySetResult();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Sem recuperação nada é reivindicado: um trabalho do boot anterior seria tratado como vivo.
                logger.LogWarning("Recuperação da fila falhou ({ErrorType}); nova tentativa em {EsperaSegundos}s.",
                    exception.GetType().Name, espera.TotalSeconds);
                try
                {
                    await Task.Delay(espera, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                espera = espera * 2 > EsperaMaximaAposFalha ? EsperaMaximaAposFalha : espera * 2;
            }
        }
    }

    private async Task ProcessarAsync(TrabalhoReivindicado trabalho)
    {
        await Task.Yield();
        metricas.Reivindicados.Add(1);
        logger.LogInformation(
            "Trabalho {IdTrabalho} reivindicado: tipo {Tipo}, geração {Geracao}, tentativa {Tentativas}/{MaximoDeTentativas}, chave {Chave}.",
            trabalho.Id, trabalho.Tipo, trabalho.Geracao, trabalho.Tentativas, trabalho.MaximoDeTentativas,
            trabalho.ChaveDeDeduplicacao);
        try
        {
            var avaliacao = await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>()
                .AvaliarAsync(trabalho, CancellationToken.None));
            switch (avaliacao.Acao)
            {
                case AcaoDoTrabalho.Descartar:
                    await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>()
                        .DescartarAsync(trabalho.Posse, avaliacao.Motivo ?? "Nada a avançar.", CancellationToken.None));
                    metricas.Descartados.Add(1);
                    logger.LogInformation("Trabalho {IdTrabalho} descartado: {Motivo}", trabalho.Id, avaliacao.Motivo);
                    break;
                case AcaoDoTrabalho.Interromper:
                    var interrupcao = await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>()
                        .InterromperAsync(trabalho.Posse, avaliacao.Alvo!, avaliacao.Motivo!, CancellationToken.None));
                    RegistrarInterrupcao(trabalho, interrupcao);
                    await AvisarAsync(avaliacao.Alvo!, interrupcao);
                    if (interrupcao.NovoEstado == EstadoDaExecucao.Pendente) sinal.Sinalizar();
                    break;
                default:
                    await ExecutarAsync(trabalho, avaliacao.Alvo!);
                    break;
            }
        }
        catch (PosseDoTrabalhoPerdidaException)
        {
            metricas.LeasesPerdidos.Add(1);
            logger.LogWarning("Posse do trabalho {IdTrabalho} (geração {Geracao}) perdida: nada foi gravado por este processo.",
                trabalho.Id, trabalho.Geracao);
        }
        catch (Exception exception)
        {
            await DevolverComBackoffAsync(trabalho, exception.GetType().Name);
        }
    }

    private async Task ExecutarAsync(TrabalhoReivindicado trabalho, AlvoDoTrabalhoDto alvo)
    {
        var executor = executores.FirstOrDefault(e => e.Tipo == alvo.Tipo);
        if (executor is null)
        {
            if (trabalho.Tentativas >= trabalho.MaximoDeTentativas)
                await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>().ConcluirEtapaAsync(trabalho.Posse,
                    alvo.IdEtapa, new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: $"Nenhum executor para execuções {alvo.Tipo}."),
                    CancellationToken.None));
            else await DevolverAsync(trabalho, relogio.GetUtcNow() + opcoes.BackoffMaximo, "sem executor", true);
            logger.LogWarning("Nenhum executor para execuções {Tipo}; trabalho {IdTrabalho} devolvido.", alvo.Tipo, trabalho.Id);
            return;
        }
        if (!executor.PodeExecutar(alvo))
        {
            var cancelada = await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>()
                .CancelarNaoIniciadaAsync(trabalho.Posse, alvo, MotivoSemExecutorLocal, CancellationToken.None));
            RegistrarInterrupcao(trabalho, cancelada);
            await AvisarAsync(alvo, cancelada, executor);
            return;
        }

        using var posseCancelada = new CancellationTokenSource();
        using var execucao = CancellationTokenSource.CreateLinkedTokenSource(posseCancelada.Token, encerramento);
        var heartbeat = new Heartbeat(this, trabalho, posseCancelada);
        var controle = new ExecucaoSobPosse(escopos, trabalho, alvo, posseCancelada.Token, heartbeat.PararAsync);
        try
        {
            logger.LogInformation("Etapa {Etapa} de {Execucao} em execução pelo trabalho {IdTrabalho} (geração {Geracao}).",
                alvo.NumeroDaEtapa, alvo.Codigo, trabalho.Id, trabalho.Geracao);
            await executor.ExecutarAsync(alvo, controle, execucao.Token);
        }
        catch (PosseDoTrabalhoPerdidaException)
        {
            // Tratado abaixo, pelo estado do controle.
        }
        catch (Exception exception) when (!posseCancelada.IsCancellationRequested)
        {
            logger.LogWarning("Executor de {Tipo} falhou no trabalho {IdTrabalho} ({ErrorType}).", alvo.Tipo, trabalho.Id,
                exception.GetType().Name);
            if (!controle.Iniciada)
            {
                await heartbeat.PararAsync();
                await DevolverComBackoffAsync(trabalho, exception.GetType().Name);
                return;
            }
        }
        finally
        {
            await heartbeat.PararAsync();
        }

        if (controle.Concluida)
        {
            metricas.Concluidos.Add(1);
            logger.LogInformation("Etapa {Etapa} de {Execucao} gravada; trabalho {IdTrabalho} concluído.", alvo.NumeroDaEtapa,
                alvo.Codigo, trabalho.Id);
            return;
        }
        if (posseCancelada.IsCancellationRequested)
        {
            logger.LogWarning("Lease do trabalho {IdTrabalho} (geração {Geracao}) perdido durante a etapa: resultado descartado.",
                trabalho.Id, trabalho.Geracao);
            return;
        }
        // Encerramento do Worker ou executor que saiu sem resultado: o trabalho volta à fila já; na próxima
        // reivindicação, uma etapa que chegou a iniciar segue a política de retomada.
        await DevolverAsync(trabalho, relogio.GetUtcNow(), "etapa sem resultado", false);
    }

    private const string MotivoSemExecutorLocal =
        "A execução não chegou a iniciar e este processo não tem o pedido (reinício do Worker). Reenvie se ainda precisar.";

    private void RegistrarInterrupcao(TrabalhoReivindicado trabalho, ExecucaoInterrompidaDto interrupcao)
    {
        metricas.Interrompidos.Add(1);
        logger.LogWarning(
            "Execução {Execucao} (etapa {Etapa}, política {Politica}, escrita {Escrita}) passou a {NovoEstado} pela recuperação do trabalho {IdTrabalho}: {Motivo}",
            interrupcao.Alvo.Codigo, interrupcao.Alvo.NumeroDaEtapa, interrupcao.Alvo.PoliticaDeRetomada,
            interrupcao.Alvo.Escrita, interrupcao.NovoEstado, trabalho.Id, interrupcao.Motivo);
    }

    private async Task AvisarAsync(AlvoDoTrabalhoDto alvo, ExecucaoInterrompidaDto interrupcao,
        IExecutorDeEtapa? executor = null)
    {
        executor ??= executores.FirstOrDefault(e => e.Tipo == alvo.Tipo);
        if (executor is null) return;
        try
        {
            await executor.AoInterromperAsync(interrupcao, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao avisar a interrupção de {Execucao} ({ErrorType}).", alvo.Codigo, exception.GetType().Name);
        }
    }

    // Erro transitório do D.A.N.T.E. (banco, conflito): backoff exponencial; esgotadas as tentativas, a execução falha.
    private async Task DevolverComBackoffAsync(TrabalhoReivindicado trabalho, string erro)
    {
        metricas.Retentativas.Add(1);
        try
        {
            if (trabalho.Tentativas >= trabalho.MaximoDeTentativas &&
                await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>().AvaliarAsync(trabalho,
                    CancellationToken.None)) is { Alvo: { } alvo })
            {
                await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>().ConcluirEtapaAsync(trabalho.Posse,
                    alvo.IdEtapa, new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: $"Tentativas esgotadas na fila ({erro})."),
                    CancellationToken.None));
                logger.LogWarning("Trabalho {IdTrabalho} esgotou {Tentativas} tentativas ({ErrorType}); execução {Execucao} falhou.",
                    trabalho.Id, trabalho.Tentativas, erro, alvo.Codigo);
                return;
            }
            var espera = TrabalhoDaFila.Backoff(trabalho.Tentativas, opcoes.BackoffInicial, opcoes.BackoffMaximo);
            await DevolverAsync(trabalho, relogio.GetUtcNow() + espera, erro, true);
            logger.LogWarning("Trabalho {IdTrabalho} devolvido para nova tentativa em {EsperaSegundos}s ({ErrorType}); tentativa {Tentativas}/{MaximoDeTentativas}.",
                trabalho.Id, espera.TotalSeconds, erro, trabalho.Tentativas, trabalho.MaximoDeTentativas);
        }
        catch (Exception exception)
        {
            // Sem banco nem para devolver: o lease vence e outra reivindicação retoma pelo mesmo caminho.
            logger.LogWarning("Não foi possível devolver o trabalho {IdTrabalho} ({ErrorType}); o lease vencido o devolve.",
                trabalho.Id, exception.GetType().Name);
        }
    }

    private async Task DevolverAsync(TrabalhoReivindicado trabalho, DateTimeOffset disponivelEm, string motivo, bool contar)
    {
        var devolvido = await EmEscopoAsync(s => s.GetRequiredService<IFilaDeTrabalho>().DevolverAsync(trabalho.Posse,
            disponivelEm, motivo, contar, CancellationToken.None));
        if (!devolvido)
            logger.LogWarning("Trabalho {IdTrabalho} não devolvido: posse perdida.", trabalho.Id);
        else sinal.Sinalizar();
    }

    private async Task<T> EmEscopoAsync<T>(Func<IServiceProvider, Task<T>> acao)
    {
        await using var escopo = escopos.CreateAsyncScope();
        return await acao(escopo.ServiceProvider);
    }

    private async Task EmEscopoAsync(Func<IServiceProvider, Task> acao)
    {
        await using var escopo = escopos.CreateAsyncScope();
        await acao(escopo.ServiceProvider);
    }

    // Renova o lease a cada Heartbeat enquanto a etapa roda. Recusa (outra geração, lease vencido) ou falhas até o
    // lease vencer cancelam a posse: o executor encerra o processo e nada mais é gravado por este processador.
    private sealed class Heartbeat
    {
        private readonly CancellationTokenSource parar = new();
        private readonly Task laco;
        private int parado;

        internal Heartbeat(ProcessadorDaFila processador, TrabalhoReivindicado trabalho, CancellationTokenSource perdida) =>
            laco = Task.Run(() => RenovarAsync(processador, trabalho, perdida));

        internal async Task PararAsync()
        {
            if (Interlocked.Exchange(ref parado, 1) == 0) await parar.CancelAsync();
            try
            {
                await laco;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RenovarAsync(ProcessadorDaFila processador, TrabalhoReivindicado trabalho,
            CancellationTokenSource perdida)
        {
            var leaseAte = trabalho.LeaseAte;
            while (true)
            {
                await Task.Delay(processador.opcoes.Heartbeat, parar.Token);
                var antes = processador.relogio.GetUtcNow();
                bool renovado;
                try
                {
                    renovado = await processador.EmEscopoAsync(s => s.GetRequiredService<IFilaDeTrabalho>()
                        .RenovarAsync(trabalho.Posse, processador.opcoes.Lease, parar.Token));
                    if (renovado) leaseAte = antes + processador.opcoes.Lease;
                }
                catch (OperationCanceledException) when (parar.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // Falha isolada de heartbeat: a posse vale até o lease vencer; depois, perdida.
                    renovado = processador.relogio.GetUtcNow() < leaseAte;
                    processador.logger.LogWarning("Heartbeat do trabalho {IdTrabalho} falhou ({ErrorType}); lease até {LeaseAte:O}.",
                        trabalho.Id, exception.GetType().Name, leaseAte);
                }
                if (renovado) continue;
                if (parar.IsCancellationRequested) return;
                processador.metricas.LeasesPerdidos.Add(1);
                processador.logger.LogWarning("Heartbeat recusado: posse do trabalho {IdTrabalho} (geração {Geracao}) perdida; a etapa é cancelada localmente.",
                    trabalho.Id, trabalho.Geracao);
                await perdida.CancelAsync();
                return;
            }
        }
    }
}
