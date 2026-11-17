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
// Tarefa de escrita (#307, AD-71): depois das conferências e antes de qualquer sessão, planeja uma worktree por
// repositório do contexto (principal e vinculados), reserva todas atomicamente sob posse, cria ou adota cada uma,
// valida e só então marca prontas e confere a guarda com elas. Qualquer recusa falha a etapa sem processo; capacidade
// esgotada devolve o trabalho sem contar tentativa. A limpeza física (remove o que ficou limpo, preserva com motivo o
// que tem alterações) é do processador, depois de qualquer desfecho da etapa de escrita.
public sealed class ExecutorDeTarefasDaFila(
    IServiceScopeFactory escopos,
    JobRegistry jobs,
    EspelhoDoLedger espelho,
    SinalDaFila sinal,
    ILogger<ExecutorDeTarefasDaFila> logger,
    ITelegramBotApi? telegram = null,
    IdentidadeDoProcessador? identidade = null,
    IGerenciadorDeWorktrees? gerenciador = null) : IExecutorDeEtapa
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
        var plano = entrada.Plano;
        if (!entrada.Abortada && await ConferirAsync(alvo, plano, null, cancellationToken) is { } recusa)
        {
            await RecusarAsync(alvo, controle, recusa);
            return;
        }
        try
        {
            if (!entrada.Abortada && plano.Escrita &&
                (await PrepararWorktreesAsync(alvo, controle, plano, cancellationToken) ??
                 await ConferirAsync(alvo, plano, controle.Worktrees, cancellationToken)) is { } recusaDaWorktree)
            {
                await RecusarAsync(alvo, controle, recusaDaWorktree);
                return;
            }
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

    // Recusa definitiva antes de qualquer processo: a etapa não chega a iniciar e a execução falha com o motivo.
    private async Task RecusarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, string recusa)
    {
        logger.LogWarning("Tarefa {Execucao} recusada antes de iniciar: {Motivo}", alvo.Codigo, recusa);
        await controle.ConcluirAsync(new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: recusa), CancellationToken.None);
        entradas.TryRemove(alvo.NumeroDaExecucao, out _);
        await AvisarAsync(alvo, controle.EstadoFinal == EstadoDaEtapa.Cancelada
            ? Cancelada(alvo.Codigo, null) : $"Tarefa {alvo.Codigo} não foi iniciada: {recusa}", CancellationToken.None);
    }

    // Texto: recusa definitiva (nada é entregue ao agente). CapacidadeDeWorktreesIndisponivelException e posse perdida
    // propagam ao processador. Uma reserva já existente da execução (outra tentativa) é reutilizada e revalidada.
    private async Task<string?> PrepararWorktreesAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle,
        PlanoDaTarefa plano, CancellationToken cancellationToken)
    {
        if (gerenciador is null) return "worktrees de execução indisponíveis neste Worker.";
        var contexto = plano.Contexto;
        if (contexto.Mode != Dante.Application.Contextos.JobExecutionMode.Repository || contexto.RepositoryAlias is null)
            return "tarefa de escrita exige repositório.";
        Dante.Application.Contextos.RepositorioCadastrado[] repositorios =
            [new(contexto.RepositoryAlias, contexto.WorkingDirectory), .. contexto.RepositoriosVinculados];
        var planejadas = new List<WorktreePlanejada>();
        try
        {
            foreach (var repositorio in repositorios)
                planejadas.Add(await gerenciador.PlanejarAsync(repositorio, alvo.NumeroDaExecucao, plano.Handle,
                    cancellationToken));
        }
        catch (WorktreeInvalidaException exception)
        {
            return exception.Message;
        }
        IReadOnlyList<WorktreeDaExecucaoDto> reservas;
        try
        {
            reservas = await controle.ReservarWorktreesAsync(planejadas, Maquina, cancellationToken);
        }
        catch (WorktreeInvalidaException exception)
        {
            return exception.Message;
        }
        foreach (var reserva in reservas)
        {
            try
            {
                await gerenciador.CriarOuAdotarAsync(reserva.Planejada, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Todas ou nenhuma: esta falha e as demais são liberadas (a limpeza remove as que ficaram limpas).
                var motivo = exception is WorktreeInvalidaException ? exception.Message
                    : $"falha ao criar a worktree ({exception.GetType().Name}).";
                logger.LogWarning("Worktree de {Alias} da tarefa {Execucao} recusada: {Motivo}", reserva.Alias,
                    alvo.Codigo, motivo);
                await controle.FalharWorktreesAsync(reserva.Alias, motivo, CancellationToken.None);
                return $"worktree de {reserva.Alias}: {motivo}";
            }
        }
        await controle.MarcarWorktreesProntasAsync(cancellationToken);
        // Imediatamente antes de entregar ao agente: nada mudou entre a criação e agora.
        foreach (var worktree in controle.Worktrees)
        {
            try
            {
                await gerenciador.ValidarAsync(worktree.Planejada, cancellationToken);
            }
            catch (WorktreeInvalidaException exception)
            {
                await controle.FalharWorktreesAsync(worktree.Alias, exception.Message, CancellationToken.None);
                return $"worktree de {worktree.Alias}: {exception.Message}";
            }
        }
        logger.LogInformation("Tarefa {Execucao}: {Quantidade} worktree(s) pronta(s) para a etapa de escrita.",
            alvo.Codigo, controle.Worktrees.Count);
        return null;
    }

    private string Maquina => identidade?.Host ?? Environment.MachineName;

    public async Task AoInterromperAsync(ExecucaoInterrompidaDto interrupcao, CancellationToken cancellationToken)
    {
        var alvo = interrupcao.Alvo;
        var temPedido = PodeExecutar(alvo);
        if (interrupcao.NovoEstado != EstadoDaExecucao.Pendente) entradas.TryRemove(alvo.NumeroDaExecucao, out _);
        var texto = interrupcao.NovoEstado switch
        {
            EstadoDaExecucao.Cancelada when interrupcao.PorCancelamento => Cancelada(alvo.Codigo, interrupcao.Motivo),
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
    // worktrees: nulo antes da preparação (a guarda de escrita é conferida depois, com as worktrees prontas).
    private async Task<string?> ConferirAsync(AlvoDoTrabalhoDto alvo, PlanoDaTarefa plano,
        IReadOnlyList<WorktreeDaExecucaoDto>? worktrees, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        var servicos = escopo.ServiceProvider;
        var etapa = await servicos.GetRequiredService<IEtapaDaExecucaoRepository>().ObterPorIdAsync(alvo.IdEtapa, cancellationToken);
        var execucao = etapa is null ? null
            : await servicos.GetRequiredService<IExecucaoRepository>().ObterPorIdAsync(etapa.IdExecucao, cancellationToken);
        if (etapa is null || execucao is null) return "execução ou etapa não registrada no ledger.";
        // O pedido e o ledger concordam sobre escrita; o plano de escrita tem a forma exata (perfil padrão da versão,
        // repositório, driver com confinamento).
        if (etapa.Escrita != plano.Escrita || (plano.Escrita && !plano.EscritaValida) || (!plano.Escrita && !plano.SomenteLeitura))
            return new EscritaSemWorktreeException("o pedido de escrita difere da etapa registrada").Message;
        try
        {
            if (!plano.Escrita || worktrees is not null) GuardaDeEscrita.Exigir(execucao, etapa, plano.Perfil, worktrees);
        }
        catch (Exception exception) when (exception is EscritaSemWorktreeException or InvalidOperationException)
        {
            return exception.Message;
        }
        if (worktrees is not null) return null;
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

    // Aviso do cancelamento pedido pelo dono (#305), dado uma vez, depois da transição gravada.
    internal static string Cancelada(string codigo, string? motivo) =>
        $"Tarefa {codigo} cancelada{(motivo is null ? "" : $" ({motivo})")}. Nenhum resultado será entregue.";

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
