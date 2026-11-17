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
                    "Recuperação da fila: {UnidadesParadas} unidade(s) de boot anterior parada(s), {UnidadesDesconhecidas} desconhecida(s) relatada(s) sem parar, {FalhasAoParar} falha(s) ao parar, listagem indisponível: {ListagemIndisponivel}; {SessoesInterrompidas} sessão(ões) interrompida(s), {SessoesPendentes} pendente(s) sem confirmação de parada; {TrabalhosDevolvidos} trabalho(s) de boot anterior devolvido(s). Boot {Boot}.",
                    relatorio.UnidadesParadas.Count, relatorio.UnidadesDesconhecidas.Count, relatorio.FalhasAoParar.Count,
                    relatorio.ListagemIndisponivel, relatorio.SessoesInterrompidas, relatorio.SessoesPendentes,
                    relatorio.TrabalhosDevolvidos,
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
            // Limite antes de executar ou retomar: a reivindicação já contou esta tentativa, inclusive quando recuperou
            // o lease vencido de um processo morto (SIGKILL) que nunca devolveu o trabalho. Além do limite, nenhum
            // executor é iniciado e a política da etapa decide o estado terminal.
            if (avaliacao.Acao != AcaoDoTrabalho.Descartar && Esgotado(trabalho))
            {
                await EncerrarPorTentativasEsgotadasAsync(trabalho, avaliacao.Alvo!,
                    $"Tentativas esgotadas na fila ({trabalho.MaximoDeTentativas}): a tentativa anterior não terminou.");
                return;
            }
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
                    await ExecutarAsync(trabalho, avaliacao.Alvo!, avaliacao.Origem);
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

    private async Task ExecutarAsync(TrabalhoReivindicado trabalho, AlvoDoTrabalhoDto alvo, string? origem)
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
            // O pedido pode estar na memória de um processo vivo em outro host: devolve para ele sem contar tentativa e
            // nunca cancela daqui. Só cancela quando a origem é deste host (este boot ou um boot morto, AD-28); um
            // produtor de outro host que morreu é tratado quando aquele host reinicia.
            if (origem is not null && !identidade.MesmoHost(origem))
            {
                await DevolverAsync(trabalho, relogio.GetUtcNow() + opcoes.BackoffMaximo, "pedido em outro processo", false);
                logger.LogInformation("Trabalho {IdTrabalho} devolvido: o pedido está em outro processo ({Origem}).", trabalho.Id, origem);
                return;
            }
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
        string? falha = null;
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
            falha = exception.GetType().Name;
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
        // Falha do executor: backoff contando tentativa (esgotadas, a execução falha), mesmo que a etapa tenha iniciado,
        // para uma falha persistente não girar pela retomada automática.
        if (falha is not null && !encerramento.IsCancellationRequested)
        {
            await DevolverComBackoffAsync(trabalho, falha);
            return;
        }
        // Encerramento do Worker ou executor que saiu sem resultado: o trabalho volta à fila já; na próxima
        // reivindicação, uma etapa que chegou a iniciar segue a política de retomada. Só o encerramento do Worker não
        // conta tentativa: um executor que sai sem resultado repetidamente esgota o limite como qualquer outra falha.
        await DevolverAsync(trabalho, relogio.GetUtcNow(), "etapa sem resultado", !encerramento.IsCancellationRequested);
    }

    // Tentativas inclui a reivindicação atual: a de número MaximoDeTentativas ainda roda; a seguinte, não.
    private static bool Esgotado(TrabalhoReivindicado trabalho) => trabalho.Tentativas > trabalho.MaximoDeTentativas;

    private async Task EncerrarPorTentativasEsgotadasAsync(TrabalhoReivindicado trabalho, AlvoDoTrabalhoDto alvo,
        string motivo)
    {
        var encerrada = await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>()
            .EncerrarPorTentativasEsgotadasAsync(trabalho.Posse, alvo, motivo, CancellationToken.None));
        metricas.Esgotados.Add(1);
        logger.LogWarning(
            "Trabalho {IdTrabalho} esgotou as tentativas ({Tentativas}/{MaximoDeTentativas}); execução {Execucao} (etapa {Etapa}, política {Politica}) passou a {NovoEstado}.",
            trabalho.Id, trabalho.Tentativas, trabalho.MaximoDeTentativas, alvo.Codigo, alvo.NumeroDaEtapa,
            alvo.PoliticaDeRetomada, encerrada.NovoEstado);
        await AvisarAsync(alvo, encerrada);
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
            // Esta era a última tentativa: a política da etapa decide o estado terminal (uma etapa Nunca que chegou a
            // iniciar fica Bloqueada, nunca vira falha comum).
            if (trabalho.Tentativas >= trabalho.MaximoDeTentativas &&
                await EmEscopoAsync(s => s.GetRequiredService<IFilaDeExecucoesAppService>().AvaliarAsync(trabalho,
                    CancellationToken.None)) is { Acao: not AcaoDoTrabalho.Descartar, Alvo: { } alvo })
            {
                await EncerrarPorTentativasEsgotadasAsync(trabalho, alvo, $"Tentativas esgotadas na fila ({erro}).");
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
    // Além do laço, um prazo independente (CancelAfter) cancela a posse local antes de o lease vencer: uma renovação
    // presa no banco não mantém a CLI rodando depois de outro processador poder assumir o trabalho.
    private sealed class Heartbeat
    {
        private readonly CancellationTokenSource parar = new();
        private readonly CancellationTokenSource perdida;
        private readonly ProcessadorDaFila processador;
        private readonly Task laco;
        private readonly CancellationTokenRegistration aviso;
        private int parado;

        internal Heartbeat(ProcessadorDaFila processador, TrabalhoReivindicado trabalho, CancellationTokenSource perdida)
        {
            this.processador = processador;
            this.perdida = perdida;
            aviso = perdida.Token.Register(() =>
            {
                if (Volatile.Read(ref parado) == 1) return;
                processador.metricas.LeasesPerdidos.Add(1);
                processador.logger.LogWarning("Posse do trabalho {IdTrabalho} (geração {Geracao}) perdida; a etapa é cancelada localmente.",
                    trabalho.Id, trabalho.Geracao);
            });
            Armar(trabalho.LeaseAte);
            laco = Task.Run(() => RenovarAsync(trabalho));
        }

        internal async Task PararAsync()
        {
            if (Interlocked.Exchange(ref parado, 1) == 0)
            {
                Desarmar();
                await parar.CancelAsync();
            }
            try
            {
                await laco;
            }
            catch (OperationCanceledException)
            {
            }
            await aviso.DisposeAsync();
        }

        // Prazo local: o lease menos uma margem (um décimo do lease, no máximo 1 s) para a CLI parar antes de o banco
        // considerar o trabalho livre.
        private void Armar(DateTimeOffset leaseAte)
        {
            var margem = processador.opcoes.Lease / 10 < TimeSpan.FromSeconds(1) ? processador.opcoes.Lease / 10 : TimeSpan.FromSeconds(1);
            var prazo = leaseAte - margem - processador.relogio.GetUtcNow();
            try
            {
                if (prazo <= TimeSpan.Zero) perdida.Cancel();
                else perdida.CancelAfter(prazo);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Desarmar()
        {
            try
            {
                perdida.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task RenovarAsync(TrabalhoReivindicado trabalho)
        {
            var opcoes = processador.opcoes;
            var leaseAte = trabalho.LeaseAte;
            var proxima = opcoes.Heartbeat;
            while (true)
            {
                // Nunca dorme além do lease: sem renovação até lá, a posse é dada como perdida no prazo.
                var restante = leaseAte - processador.relogio.GetUtcNow();
                var espera = proxima < restante ? proxima : restante;
                if (espera > TimeSpan.Zero) await Task.Delay(espera, parar.Token);
                var antes = processador.relogio.GetUtcNow();
                bool renovado;
                if (antes >= leaseAte) renovado = false;
                else
                {
                    using var prazo = CancellationTokenSource.CreateLinkedTokenSource(parar.Token);
                    prazo.CancelAfter(leaseAte - antes);
                    try
                    {
                        renovado = await processador.EmEscopoAsync(s => s.GetRequiredService<IFilaDeTrabalho>()
                            .RenovarAsync(trabalho.Posse, opcoes.Lease, prazo.Token));
                        if (renovado && !perdida.IsCancellationRequested && Volatile.Read(ref parado) == 0)
                        {
                            leaseAte = antes + opcoes.Lease;
                            proxima = opcoes.Heartbeat;
                            Armar(leaseAte);
                        }
                    }
                    catch (OperationCanceledException) when (parar.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        // Falha isolada (banco lento ou fora): tenta de novo mais cedo; a posse vale só até o lease.
                        renovado = processador.relogio.GetUtcNow() < leaseAte;
                        proxima = opcoes.Heartbeat / 4;
                        processador.logger.LogWarning("Heartbeat do trabalho {IdTrabalho} falhou ({ErrorType}); lease até {LeaseAte:O}.",
                            trabalho.Id, exception.GetType().Name, leaseAte);
                    }
                }
                if (renovado && !perdida.IsCancellationRequested) continue;
                if (parar.IsCancellationRequested) return;
                await perdida.CancelAsync();
                return;
            }
        }
    }
}
