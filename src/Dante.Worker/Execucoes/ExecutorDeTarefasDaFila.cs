using System.Collections.Concurrent;
using Dante.Application.Agentes;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Jobs;
using Dante.Worker.Telegram;

namespace Dante.Worker.Execucoes;

// /tarefa pela fila durável (AD-67, AD-68): o único caminho que executa uma Tarefa. Execução, etapa somente leitura,
// evento e trabalho são gravados numa transação antes da confirmação ao usuário; o processador reivindica o trabalho e
// chama de volta a rodada que o Telegram liberou, com início e resultado gravados sob posse. Antes de qualquer processo,
// este executor confere a guarda de escrita (fail-closed), a versão congelada na execução, o agente ainda ativo e o
// modelo/esforço da versão no catálogo da CLI. O pedido (texto e ambiente do repositório) fica só em memória, como o
// do job: depois de um reinício, a tarefa que este processo não tem é cancelada com aviso, nunca executada às cegas.
public sealed class ExecutorDeTarefasDaFila(
    IServiceScopeFactory escopos,
    JobRegistry jobs,
    EspelhoDoLedger espelho,
    SinalDaFila sinal,
    ILogger<ExecutorDeTarefasDaFila> logger,
    ITelegramBotApi? telegram = null,
    IdentidadeDoProcessador? identidade = null) : IExecutorDeEtapa
{
    public const string FilaIndisponivel =
        "Fila de execuções indisponível (banco de dados). Nada foi enviado ao agente; tente novamente.";

    // A rodada da tarefa: abre a sessão reservada, roda o turno fora de transação, grava o resultado sob posse e só
    // então responde. O token é cancelado no encerramento do Worker e quando a posse se perde.
    public delegate Task ExecutarTarefa(ExecucaoSobPosse controle, CancellationToken cancellationToken);

    private readonly ConcurrentDictionary<long, Entrada> entradas = new();

    public TipoDeExecucao Tipo => TipoDeExecucao.Tarefa;

    public bool PodeExecutar(AlvoDoTrabalhoDto alvo) => entradas.ContainsKey(alvo.NumeroDaExecucao);

    // Reserva o número e grava execução + trabalho numa transação. Recusa quando a chave já foi enfileirada (mesma
    // mensagem do Telegram) ou o banco não responde: nada é executado sem registro durável.
    public async Task<CriacaoDaTarefa> CriarAsync(PlanoDaTarefa plano, long idDoUsuario, string chaveDeDeduplicacao,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plano);
        try
        {
            await using var consulta = escopos.CreateAsyncScope();
            if (await consulta.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>()
                    .ObterJobPorChaveAsync(chaveDeDeduplicacao, cancellationToken) is { } existente)
                return Duplicada($"E{existente.Numero}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Fila indisponível ao conferir a chave da tarefa ({ErrorType}).", exception.GetType().Name);
            return new CriacaoDaTarefa(null, null, FilaIndisponivel);
        }
        if (await espelho.ReservarNumeroDeExecucaoAsync(jobs.LastNumber, cancellationToken) is not { } numero)
            return new CriacaoDaTarefa(null, null, FilaIndisponivel);
        // Antes do commit: o processador pode reivindicar assim que o trabalho existir.
        entradas[numero] = new Entrada(plano);
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var enfileirada = await escopo.ServiceProvider.GetRequiredService<IFilaDeExecucoesAppService>()
                .EnfileirarTarefaAsync(plano, numero, LedgerChannel.Telegram, idDoUsuario, chaveDeDeduplicacao,
                    cancellationToken, identidade);
            if (!enfileirada.Novo)
            {
                entradas.TryRemove(numero, out _);
                return Duplicada(enfileirada.Codigo);
            }
            logger.LogInformation("Tarefa {Execucao} do agente {Agente} v{Versao} enfileirada na fila durável.",
                enfileirada.Codigo, plano.Handle, plano.Versao.Numero);
            sinal.Sinalizar();
            return new CriacaoDaTarefa(numero, enfileirada.Codigo, null);
        }
        catch (Exception exception)
        {
            entradas.TryRemove(numero, out _);
            if (exception is OperationCanceledException) throw;
            if (exception is ConflitoDeConcorrenciaException) return Duplicada(null);
            logger.LogWarning("Falha ao enfileirar a tarefa E{Numero} ({ErrorType}).", numero, exception.GetType().Name);
            return new CriacaoDaTarefa(null, null, FilaIndisponivel);
        }
    }

    // A tarefa foi confirmada ao usuário: o processador pode executá-la (e retomá-la, enquanto este processo a tiver).
    public void Liberar(long numero, ExecutarTarefa executar)
    {
        if (entradas.TryGetValue(numero, out var entrada)) entrada.Liberacao.TrySetResult(executar);
    }

    // A confirmação falhou: quando reivindicada, a execução termina Falhou sem sessão nem processo.
    public void Abortar(long numero, string erro)
    {
        if (!entradas.TryGetValue(numero, out var entrada)) return;
        entrada.Abortada = true;
        entrada.Liberacao.TrySetResult((controle, _) => controle.ConcluirAsync(
            new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: erro), CancellationToken.None));
    }

    public async Task ExecutarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, CancellationToken cancellationToken)
    {
        if (!entradas.TryGetValue(alvo.NumeroDaExecucao, out var entrada))
            throw new InvalidOperationException("Tarefa sem pedido neste processo.");
        var executar = await entrada.Liberacao.Task.WaitAsync(cancellationToken);
        if (!entrada.Abortada && await ConferirAsync(alvo, entrada.Plano, cancellationToken) is { } recusa)
        {
            // Recusa definitiva antes de qualquer processo: a etapa não chega a iniciar e a execução falha com o motivo.
            logger.LogWarning("Tarefa {Execucao} recusada antes de iniciar: {Motivo}", alvo.Codigo, recusa);
            await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: recusa), CancellationToken.None);
            entradas.TryRemove(alvo.NumeroDaExecucao, out _);
            await AvisarAsync(alvo, $"Tarefa {alvo.Codigo} não foi iniciada: {recusa}", cancellationToken);
            return;
        }
        try
        {
            await executar(controle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Posse perdida ou encerramento do Worker: a sessão já foi encerrada e nada foi gravado. O processador decide
            // pelo estado da posse (descarta ou devolve), como faz com o job.
            return;
        }
        // Gravada: nada mais a retomar. Sem resultado (posse perdida, encerramento), o pedido continua aqui para a
        // retomada automática deste mesmo processo.
        if (controle.Concluida) entradas.TryRemove(alvo.NumeroDaExecucao, out _);
    }

    public async Task AoInterromperAsync(ExecucaoInterrompidaDto interrupcao, CancellationToken cancellationToken)
    {
        var alvo = interrupcao.Alvo;
        var temPedido = PodeExecutar(alvo);
        if (interrupcao.NovoEstado != EstadoDaExecucao.Pendente) entradas.TryRemove(alvo.NumeroDaExecucao, out _);
        var texto = interrupcao.NovoEstado switch
        {
            EstadoDaExecucao.Cancelada =>
                $"Tarefa {alvo.Codigo} não pôde continuar depois do reinício do Worker e foi cancelada: o pedido da " +
                "tarefa não é guardado. Reenvie se ainda precisar.",
            // Sem o pedido neste processo, a retomada termina cancelada logo em seguida, com o aviso acima.
            EstadoDaExecucao.Pendente when temPedido => $"Tarefa {alvo.Codigo} foi interrompida e será retomada automaticamente.",
            EstadoDaExecucao.Pendente => null,
            EstadoDaExecucao.Falhou =>
                $"Tarefa {alvo.Codigo} falhou: esgotou as tentativas de execução da fila e não será repetida automaticamente.",
            _ => $"Tarefa {alvo.Codigo} foi interrompida ({interrupcao.NovoEstado}): {interrupcao.Motivo}"
        };
        if (texto is not null) await AvisarAsync(alvo, texto, cancellationToken);
    }

    // Guarda e conferências que valem a cada tentativa. Texto: recusa definitiva. Exceção: transitória (banco ou
    // catálogo indisponível), devolvida pelo processador com backoff, contando tentativa.
    private async Task<string?> ConferirAsync(AlvoDoTrabalhoDto alvo, PlanoDaTarefa plano, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        var servicos = escopo.ServiceProvider;
        var etapa = await servicos.GetRequiredService<IEtapaDaExecucaoRepository>().ObterPorIdAsync(alvo.IdEtapa, cancellationToken);
        var execucao = etapa is null ? null
            : await servicos.GetRequiredService<IExecucaoRepository>().ObterPorIdAsync(etapa.IdExecucao, cancellationToken);
        if (etapa is null || execucao is null) return "execução ou etapa não registrada no ledger.";
        try
        {
            GuardaDeEscrita.Exigir(execucao, etapa, plano.Perfil);
        }
        catch (Exception exception) when (exception is EscritaSemWorktreeException or InvalidOperationException)
        {
            return exception.Message;
        }
        // Versão congelada: a execução aponta para a versão do pedido, e a etapa usa driver, modelo e esforço dela.
        var configuracao = plano.Versao.Configuracao;
        if (execucao.IdVersaoDoAgente != plano.Versao.Id || etapa.Agente != configuracao.Driver ||
            !string.Equals(etapa.Modelo, configuracao.Modelo, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(etapa.Esforco, configuracao.Esforco, StringComparison.OrdinalIgnoreCase))
            return "a versão do agente registrada na execução difere da do pedido.";
        var cadastro = servicos.GetRequiredService<ICadastroDeAgentesAppService>();
        // Desativado, arquivado ou com versão cujo hash não confere: nada é iniciado. Uma versão nova não substitui a
        // congelada.
        var resolucao = await cadastro.ResolverAsync(plano.Handle, cancellationToken);
        if (!resolucao.Resolvido) return resolucao.Erro ?? $"o agente {plano.Handle} não está disponível.";
        try
        {
            await cadastro.ConferirVersaoNoCatalogoAsync(plano.Versao, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
        return null;
    }

    private async Task AvisarAsync(AlvoDoTrabalhoDto alvo, string texto, CancellationToken cancellationToken)
    {
        if (telegram is null || alvo.Canal != LedgerChannel.Telegram) return;
        try
        {
            // No chat privado do Telegram, o id do chat é o id do usuário.
            await telegram.SendMessageAsync(alvo.IdDoUsuarioNoCanal, texto, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Falha ao avisar sobre a tarefa {Execucao} ({ErrorType}).", alvo.Codigo, exception.GetType().Name);
        }
    }

    private CriacaoDaTarefa Duplicada(string? codigo)
    {
        logger.LogInformation("Pedido de tarefa deduplicado pela chave da fila ({Execucao}).", codigo ?? "corrida");
        return new CriacaoDaTarefa(null, null,
            codigo is null ? "Pedido já recebido." : $"Pedido já recebido como {codigo}.");
    }

    private sealed class Entrada(PlanoDaTarefa plano)
    {
        public PlanoDaTarefa Plano { get; } = plano;
        public volatile bool Abortada;
        public TaskCompletionSource<ExecutarTarefa> Liberacao { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// Tarefa enfileirada (número e código E…) ou o motivo da recusa, já pronto para o usuário.
public sealed record CriacaoDaTarefa(long? Numero, string? Codigo, string? Recusa);
