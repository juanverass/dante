using System.Collections.Concurrent;
using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;
using Dante.Worker.Telegram;

namespace Dante.Worker.Jobs;

// Jobs /claude e /codex pelo caminho da fila durável (AD-67), só com Orquestracao:Habilitada. A execução Avulsa e o
// seu trabalho são gravados juntos antes da resposta "iniciado"; o processador reivindica e chama de volta o mesmo
// RunJobAsync do legado, com início e resultado gravados sob posse. O pedido (texto, imagens, ambiente) continua só em
// memória, como hoje: depois de um reinício, um job que não iniciou é cancelado com aviso e um que estava rodando segue
// a política ComConfirmacao (nunca repetido sem confirmação).
public sealed class ExecutorDeJobsDaFila(
    IServiceScopeFactory escopos,
    JobRegistry jobs,
    EspelhoDoLedger espelho,
    SinalDaFila sinal,
    MetricasDaFila metricas,
    ILogger<ExecutorDeJobsDaFila> logger,
    ITelegramBotApi? telegram = null,
    IdentidadeDoProcessador? identidade = null) : IExecutorDeEtapa
{
    public const string FilaIndisponivel =
        "Fila de execuções indisponível (banco de dados). Nada foi enviado ao agente; tente novamente.";

    // O que roda quando o processador reivindica o job: o RunJobAsync do legado, ou o encerramento de um job que
    // falhou antes de ser confirmado ao usuário.
    public delegate Task ExecutarJob(ExecucaoSobPosse controle);

    private readonly ConcurrentDictionary<long, Entrada> entradas = new();

    public TipoDeExecucao Tipo => TipoDeExecucao.Avulsa;

    public bool PodeExecutar(AlvoDoTrabalhoDto alvo) => entradas.ContainsKey(alvo.NumeroDaExecucao);

    // Reserva o número, cria o job (Queued) e grava execução + trabalho numa transação. Recusa quando a chave já foi
    // enfileirada (mesma mensagem do Telegram) ou o banco não responde: nada é executado sem registro durável.
    public async Task<CriacaoNaFila> CriarAsync(string agent, JobExecutionContext context, long userId,
        string chaveDeDeduplicacao, AgentModelSelection? modelSelection, CancellationToken stoppingToken)
    {
        var agente = Enum.Parse<AgentKind>(agent, true);
        try
        {
            await using var consulta = escopos.CreateAsyncScope();
            if (await consulta.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>()
                    .ObterJobPorChaveAsync(chaveDeDeduplicacao, stoppingToken) is { } existente)
                return Duplicado(existente.Numero);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Fila indisponível ao conferir a chave do job ({ErrorType}).", exception.GetType().Name);
            return new CriacaoNaFila(null, default, FilaIndisponivel);
        }
        if (await espelho.ReservarNumeroDeExecucaoAsync(jobs.LastNumber, stoppingToken) is not { } numero ||
            jobs.CreateQueued(numero, agent, context, stoppingToken, modelSelection) is not { } criado)
            return new CriacaoNaFila(null, default, FilaIndisponivel);
        var (job, token) = criado;
        // Antes do commit: o processador pode reivindicar assim que o trabalho existir.
        entradas[numero] = new Entrada(job.Id);
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var enfileirado = await escopo.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>().EnfileirarJobAsync(
                new JobRegistradoDto(numero, agente, LedgerChannel.Telegram, userId, context, modelSelection?.Model,
                    modelSelection?.Effort, job.CreatedAtUtc), chaveDeDeduplicacao, stoppingToken, identidade);
            if (!enfileirado.Novo)
            {
                // Outro produtor gravou a mesma chave entre a consulta e a gravação: nada deste número existe no banco.
                entradas.TryRemove(numero, out _);
                jobs.Complete(job.Id, AgentProcessStatus.Failed, errorMessage: "Pedido já recebido.");
                return Duplicado(enfileirado.Numero);
            }
        }
        catch (Exception exception)
        {
            entradas.TryRemove(numero, out _);
            var duplicado = exception is ConflitoDeConcorrenciaException;
            jobs.Complete(job.Id, AgentProcessStatus.Failed,
                errorMessage: duplicado ? "Pedido já recebido." : FilaIndisponivel);
            if (exception is OperationCanceledException) throw;
            if (duplicado) return Duplicado(null);
            logger.LogWarning("Falha ao enfileirar o job {JobId} ({ErrorType}).", job.Id, exception.GetType().Name);
            return new CriacaoNaFila(null, default, FilaIndisponivel);
        }
        logger.LogInformation("Job {JobId} enfileirado na fila durável.", job.Id);
        sinal.Sinalizar();
        return new CriacaoNaFila(job, token, null);
    }

    // O pedido foi confirmado ao usuário: o processador pode executá-lo.
    public void Liberar(string jobId, ExecutarJob executar)
    {
        if (entradas.TryGetValue(Numero(jobId), out var entrada)) entrada.Liberacao.TrySetResult(executar);
    }

    // O job falhou antes de ser confirmado (anexos, resposta ao Telegram): termina como Failed no registry e no
    // ledger, sem processo de agente.
    public void Abortar(string jobId, string erro, Action? aoEncerrar = null) => Liberar(jobId, async controle =>
    {
        var encerrado = jobs.Complete(jobId, AgentProcessStatus.Failed, errorMessage: erro);
        aoEncerrar?.Invoke();
        await controle.ConcluirAsync(Resultado(encerrado), CancellationToken.None);
    });

    public async Task ExecutarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, CancellationToken cancellationToken)
    {
        if (!entradas.TryGetValue(alvo.NumeroDaExecucao, out var entrada))
            throw new InvalidOperationException("Job sem pedido neste processo.");
        try
        {
            var executar = await entrada.Liberacao.Task.WaitAsync(cancellationToken);
            // Posse perdida: o job é cancelado como num /cancel, e o resultado não é gravado.
            await using var cancelamento = controle.PosseCancelada.Register(() => jobs.TryCancel(entrada.JobId, out _));
            await executar(controle);
        }
        finally
        {
            entradas.TryRemove(alvo.NumeroDaExecucao, out _);
        }
    }

    public async Task AoInterromperAsync(ExecucaoInterrompidaDto interrupcao, CancellationToken cancellationToken)
    {
        // Tentativas esgotadas: o executor não roda mais este job; o pedido em memória (se deste boot) termina Failed
        // no registry em vez de ficar Queued.
        if (interrupcao.NovoEstado == EstadoDaExecucao.Falhou &&
            entradas.TryRemove(interrupcao.Alvo.NumeroDaExecucao, out var esgotada))
            jobs.Complete(esgotada.JobId, AgentProcessStatus.Failed, errorMessage: interrupcao.Motivo);
        if (telegram is null || interrupcao.Alvo.Canal != LedgerChannel.Telegram) return;
        var codigo = interrupcao.Alvo.Codigo;
        var texto = interrupcao.NovoEstado switch
        {
            EstadoDaExecucao.Cancelada =>
                $"{codigo} não chegou a iniciar antes do reinício do Worker e foi cancelado: o pedido do job não é guardado. Reenvie se ainda precisar.",
            EstadoDaExecucao.Pendente => $"{codigo} foi interrompido e será retomado automaticamente.",
            EstadoDaExecucao.Bloqueada => $"{codigo} foi interrompido com resultado incerto e não será repetido automaticamente. Confira o efeito antes de continuar.",
            EstadoDaExecucao.Falhou => $"{codigo} falhou: esgotou as tentativas de execução da fila e não será repetido automaticamente.",
            _ => $"{codigo} foi interrompido pelo reinício do Worker durante a execução e não será repetido sem confirmação: " +
                 "o resultado é incerto e o pedido do job não é guardado. Confira o estado do contexto antes de reenviar."
        };
        try
        {
            // No chat privado do Telegram, o id do chat é o id do usuário.
            await telegram.SendMessageAsync(interrupcao.Alvo.IdDoUsuarioNoCanal, texto, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Falha ao avisar a interrupção de {Execucao} ({ErrorType}).", codigo, exception.GetType().Name);
        }
    }

    internal static ResultadoDaEtapaDto Resultado(JobSnapshot job) => job.Status switch
    {
        JobStatus.Succeeded => new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida, job.ExitCode),
        JobStatus.Cancelled => new ResultadoDaEtapaDto(EstadoDaEtapa.Cancelada, job.ExitCode),
        _ => new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, job.ExitCode, job.ErrorMessage)
    };

    private CriacaoNaFila Duplicado(long? numero)
    {
        metricas.Deduplicados.Add(1);
        logger.LogInformation("Pedido de job deduplicado pela chave da fila ({Execucao}).",
            numero is { } n ? $"J{n:D6}" : "corrida");
        return new CriacaoNaFila(null, default,
            numero is { } valor ? $"Pedido já recebido como J{valor:D6}." : "Pedido já recebido.");
    }

    private static long Numero(string jobId) => long.Parse(jobId.AsSpan(1));

    private sealed class Entrada(string jobId)
    {
        public string JobId { get; } = jobId;
        public TaskCompletionSource<ExecutarJob> Liberacao { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// Job criado (com o token de cancelamento) ou o motivo da recusa, já pronto para o usuário.
public sealed record CriacaoNaFila(JobSnapshot? Job, CancellationToken Token, string? Recusa);
