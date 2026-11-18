using System.Collections.Concurrent;
using System.Text;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Application.Grupos;
using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;

namespace Dante.Worker.Execucoes;

// Executor do Roteiro de um grupo (AD-69, #308): um passo por trabalho da fila, na ordem fixa gravada no ledger. Cada
// passo confere de novo a integridade do snapshot e a versão congelada do agente no catálogo da CLI, abre uma sessão
// Codex reservada e separada e grava o desfecho sob posse com a transição seguinte (RoteirosAppService).
// * implementar: reserva atomicamente uma worktree, cria, valida e escreve só nela (escrita confinada, AD-71);
// * revisar: lê o diff da worktree (hash canônico), abre uma sessão Codex em sandbox somente leitura com o
//   dante_coordenacao no escopo da revisão, e só um veredito registrado pela ferramenta conta;
// * corrigir: readquire o slot sob a trava (decisão C), revalida a worktree e escreve a partir do veredito e da
//   instrução humana;
// * o gate e a decisão humana são do ledger: nenhum processo espera a pessoa.
// Nenhum agente faz commit, push, PR ou merge, e o checkout principal nunca é diretório de trabalho.
// As sessões são resolvidas só quando um passo roda: compor a fila não arrasta o registro de sessões nem os provedores
// de ferramentas.
public sealed class ExecutorDeRoteirosDaFila(
    IServiceScopeFactory escopos,
    Func<SessionRegistry> registroDeSessoes,
    EspelhoDoLedger espelho,
    SinalDaFila sinal,
    ILogger<ExecutorDeRoteirosDaFila> logger,
    ITelegramBotApi? telegram = null,
    IdentidadeDoProcessador? identidade = null,
    IGerenciadorDeWorktrees? gerenciador = null) : IExecutorDeEtapa
{
    // Diff entregue ao revisor no prompt (o hash cobre o diff inteiro; o revisor lê o restante na worktree).
    public const int LimiteDoDiffNoPrompt = 60_000;
    public static readonly TimeSpan PrazoDoTurno = TimeSpan.FromMinutes(60);

    // Objetivo original (pode conter o que a redação protegeu) só em memória, deste processo: nunca persistido.
    private readonly ConcurrentDictionary<long, string> objetivos = new();

    private SessionRegistry Sessoes => registroDeSessoes();

    public TipoDeExecucao Tipo => TipoDeExecucao.Roteiro;

    // O estado do roteiro é durável: qualquer processo continua, pedindo o objetivo à pessoa se precisar.
    public bool PodeExecutar(AlvoDoTrabalhoDto alvo) => true;

    // Grupo resolvido, contexto e objetivo bruto → execução, snapshot e trabalho numa transação; só então a resposta.
    public async Task<CriacaoDaTarefa> CriarAsync(ResolucaoDeGrupoDto grupo, string objetivo, JobExecutionContext contexto,
        long idDoUsuario, string chaveDeDeduplicacao, long acimaDe, CancellationToken cancellationToken)
    {
        if (await espelho.ReservarNumeroDeExecucaoAsync(acimaDe, cancellationToken) is not { } numero)
            return new CriacaoDaTarefa(null, null, ExecutorDeTarefasDaFila.FilaIndisponivel);
        // Antes do commit: o processador pode reivindicar assim que o trabalho existir.
        objetivos[numero] = objetivo;
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var criado = await escopo.ServiceProvider.GetRequiredService<IRoteirosAppService>().EnfileirarAsync(
                new NovoRoteiroDto(grupo, objetivo, contexto, numero, LedgerChannel.Telegram, idDoUsuario,
                    chaveDeDeduplicacao), cancellationToken);
            if (!criado.Execucao.Novo)
            {
                objetivos.TryRemove(numero, out _);
                return new CriacaoDaTarefa(null, null, $"Pedido já recebido como {criado.Execucao.Codigo}.");
            }
            logger.LogInformation("Roteiro {Execucao} do grupo {Grupo} v{Versao} enfileirado.", criado.Execucao.Codigo,
                grupo.Nome, grupo.Versao?.Numero);
            sinal.Sinalizar();
            return new CriacaoDaTarefa(numero, criado.Execucao.Codigo, null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            objetivos.TryRemove(numero, out _);
            return new CriacaoDaTarefa(null, null, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            objetivos.TryRemove(numero, out _);
            logger.LogWarning("Falha ao enfileirar o roteiro E{Numero} ({ErrorType}).", numero, exception.GetType().Name);
            return new CriacaoDaTarefa(null, null, ExecutorDeTarefasDaFila.FilaIndisponivel);
        }
    }

    public async Task ExecutarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, CancellationToken cancellationToken)
    {
        RoteiroDaExecucaoDto roteiro;
        PassoExecutadoDto passo;
        VersaoDoAgenteDto versao;
        await using (var escopo = escopos.CreateAsyncScope())
        {
            var roteiros = escopo.ServiceProvider.GetRequiredService<IRoteirosAppService>();
            roteiro = await roteiros.ObterAsync(alvo.IdExecucao, cancellationToken) ??
                throw new InvalidOperationException("Roteiro não registrado no ledger.");
            passo = roteiro.Etapas.Single(e => e.IdEtapa == alvo.IdEtapa);
            if (passo.Passo == TipoDePassoDoRoteiro.AprovacaoHumana || passo.Papel is null)
                throw new InvalidOperationException("A aprovação humana não é executada pela fila.");
            // Integridade e versão congelada antes de qualquer processo (decisão C).
            if (await roteiros.ConferirIntegridadeAsync(alvo.IdExecucao, cancellationToken) is { } recusa)
            {
                await FalharAsync(alvo, controle, roteiro, recusa);
                return;
            }
            versao = await roteiros.ObterVersaoDoParticipanteAsync(alvo.IdExecucao, passo.Papel, cancellationToken);
            try
            {
                await escopo.ServiceProvider.GetRequiredService<ICadastroDeAgentesAppService>()
                    .ConferirVersaoNoCatalogoAsync(versao, cancellationToken);
            }
            catch (ArgumentException exception)
            {
                await FalharAsync(alvo, controle, roteiro, exception.Message);
                return;
            }
        }
        // Objetivo: o original deste processo; senão o guardado, se a redação não o alterou; senão, a pessoa (decisão B).
        var objetivo = objetivos.TryGetValue(alvo.NumeroDaExecucao, out var original) ? original
            : roteiro.ObjetivoAlteradoPelaRedacao ? null : roteiro.ObjetivoRedigido;
        if (objetivo is null)
        {
            await controle.SolicitarObjetivoAsync(CancellationToken.None);
            await AvisarAsync(roteiro, $"Roteiro {roteiro.Codigo}: a tarefa foi protegida pela redação e não está mais " +
                $"em memória. Envie-a de novo, sem credenciais: /execucao objetivo {roteiro.Codigo} <tarefa>");
            return;
        }
        if (gerenciador is null)
        {
            await FalharAsync(alvo, controle, roteiro, "worktrees de execução indisponíveis neste Worker.");
            return;
        }
        var participante = roteiro.Participantes.Single(p => p.Papel == passo.Papel);
        switch (passo.Passo)
        {
            case TipoDePassoDoRoteiro.Implementar:
                if (await PrepararWorktreeAsync(alvo, controle, roteiro, participante, cancellationToken) is { } recusaDaReserva)
                {
                    await FalharAsync(alvo, controle, roteiro, recusaDaReserva);
                    return;
                }
                await EscreverAsync(alvo, controle, roteiro, passo, participante, versao,
                    PromptDeImplementacao(roteiro, participante, objetivo), cancellationToken);
                break;
            case TipoDePassoDoRoteiro.Corrigir:
                // Sem capacidade: CapacidadeDeWorktreesIndisponivelException, o processador espera sem contar tentativa.
                await controle.ReadquirirWorktreesAsync(Maquina, cancellationToken);
                if (await ValidarWorktreesAsync(controle.Worktrees, cancellationToken) is { } invalida)
                {
                    await FalharAsync(alvo, controle, roteiro, invalida);
                    return;
                }
                var revisao = await UltimoVereditoAsync(roteiro, cancellationToken);
                await EscreverAsync(alvo, controle, roteiro, passo, participante, versao,
                    PromptDeCorrecao(roteiro, participante, objetivo, passo.Rodada, revisao, roteiro.UltimaInstrucaoDeCorrecao),
                    cancellationToken);
                break;
            default:
                await RevisarAsync(alvo, controle, roteiro, passo, participante, versao, objetivo, cancellationToken);
                break;
        }
    }

    public async Task AoInterromperAsync(ExecucaoInterrompidaDto interrupcao, CancellationToken cancellationToken)
    {
        var alvo = interrupcao.Alvo;
        var texto = interrupcao.NovoEstado switch
        {
            EstadoDaExecucao.Cancelada when interrupcao.PorCancelamento =>
                $"Roteiro {alvo.Codigo} cancelado{(interrupcao.Motivo is null ? "" : $" ({interrupcao.Motivo})")}.",
            EstadoDaExecucao.Pendente => null,
            EstadoDaExecucao.Interrompida =>
                $"Roteiro {alvo.Codigo} foi interrompido durante uma etapa de escrita ({interrupcao.Motivo}). A worktree " +
                $"continua reservada; cancele com /cancel {alvo.Codigo} se não for continuar.",
            _ => $"Roteiro {alvo.Codigo} passou a {interrupcao.NovoEstado}: {interrupcao.Motivo}"
        };
        if (interrupcao.NovoEstado is EstadoDaExecucao.Cancelada or EstadoDaExecucao.Falhou)
            objetivos.TryRemove(alvo.NumeroDaExecucao, out _);
        if (texto is not null) await AvisarAsync(alvo.Canal, alvo.IdDoUsuarioNoCanal, alvo.Codigo, texto, cancellationToken);
    }

    // Implementar ou corrigir: sessão de escrita confinada só nas worktrees prontas da execução.
    private async Task EscreverAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, RoteiroDaExecucaoDto roteiro,
        PassoExecutadoDto passo, ParticipanteDaExecucaoDto participante, VersaoDoAgenteDto versao, string prompt,
        CancellationToken cancellationToken)
    {
        var configuracao = versao.Configuracao;
        var perfil = configuracao.PerfilPadrao;
        if (perfil == AgentPermissionProfile.Plan || !configuracao.PodeEscrever)
        {
            await FalharAsync(alvo, controle, roteiro,
                $"O agente {participante.Handle} v{versao.Numero} não tem modo padrão de escrita; nada foi iniciado.");
            return;
        }
        var principal = controle.Worktrees.Single(w => w.Principal);
        var pedido = new SessionStartRequest(roteiro.IdDoUsuarioNoCanal, configuracao.Driver,
            JobExecutionContext.Repository(principal.Alias, principal.Caminho), null, perfil,
            new AgentModelSelection(configuracao.Modelo, configuracao.Esforco),
            RegisteredAgent: new AgenteDaSessao(participante.Handle, versao, Instrucoes(roteiro, participante, escreve: true))
            {
                TetoDaExecucao = perfil,
                EscritaConfinada = true,
                CheckoutsDaEscrita = [principal.CaminhoDoRepositorio]
            });
        var resultado = await TurnoAsync(alvo, controle, roteiro, pedido, prompt, lembrete: null, cancellationToken);
        await ConcluirAsync(alvo, controle, roteiro, passo, new ConclusaoDoPassoDto(resultado));
    }

    // Revisar: diff e hash da worktree, sessão Codex somente leitura com o dante_coordenacao no escopo, veredito.
    private async Task RevisarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, RoteiroDaExecucaoDto roteiro,
        PassoExecutadoDto passo, ParticipanteDaExecucaoDto participante, VersaoDoAgenteDto versao, string objetivo,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WorktreeDaExecucaoDto> worktrees;
        await using (var escopo = escopos.CreateAsyncScope())
            worktrees = (await escopo.ServiceProvider.GetRequiredService<IReservasDeWorktreeAppService>()
                .ListarDaExecucaoAsync(alvo.IdExecucao, cancellationToken))
                .Where(w => w.Estado == EstadoDaReservaDeWorktree.Pronta).ToArray();
        if (worktrees.Count(w => w.Principal) != 1 || await ValidarWorktreesAsync(worktrees, cancellationToken) is { } invalida)
        {
            await FalharAsync(alvo, controle, roteiro, "a worktree da execução não está pronta para a revisão.");
            return;
        }
        controle.UsarWorktrees(worktrees);
        var principal = worktrees.Single(w => w.Principal);
        var alteracoes = await gerenciador!.ResumirAlteracoesAsync(principal.Planejada, LimiteDoDiffNoPrompt, cancellationToken);
        var escopoDaRevisao = new EscopoDaRevisaoDto(alvo.IdExecucao, roteiro.Codigo, alvo.IdEtapa, passo.Rodada, alteracoes.Hash);
        var configuracao = versao.Configuracao;
        var pedido = new SessionStartRequest(roteiro.IdDoUsuarioNoCanal, configuracao.Driver,
            JobExecutionContext.Repository(principal.Alias, principal.Caminho), null, AgentPermissionProfile.Plan,
            new AgentModelSelection(configuracao.Modelo, configuracao.Esforco),
            RegisteredAgent: new AgenteDaSessao(participante.Handle, versao, Instrucoes(roteiro, participante, escreve: false))
            {
                TetoDaExecucao = AgentPermissionProfile.Plan,
                LeituraConfinada = true,
                EscopoDaRevisao = escopoDaRevisao,
                CheckoutsDaEscrita = [principal.CaminhoDoRepositorio]
            });
        var anterior = await UltimoVereditoAsync(roteiro, cancellationToken);
        var resultado = await TurnoAsync(alvo, controle, roteiro, pedido,
            PromptDeRevisao(roteiro, participante, objetivo, passo.Rodada, alteracoes, anterior),
            lembrete: async token => await VereditoAsync(alvo.IdEtapa, token) is null
                ? "Você ainda não registrou o veredito. Registre agora com a ferramenta entregar_resultado do " +
                  "dante_coordenacao (aprovado, requer_correcoes com os itens, ou bloqueado); texto livre não conta."
                : null,
            cancellationToken);
        if (resultado.EstadoFinal != EstadoDaEtapa.Concluida)
        {
            await ConcluirAsync(alvo, controle, roteiro, passo, new ConclusaoDoPassoDto(resultado));
            return;
        }
        var veredito = await VereditoAsync(alvo.IdEtapa, CancellationToken.None);
        if (veredito is null)
        {
            await ConcluirAsync(alvo, controle, roteiro, passo, new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(
                EstadoDaEtapa.Falhou, Erro: "O revisor terminou sem registrar o veredito pela ferramenta; nada é aprovado.")));
            return;
        }
        // O artefato decidido é o revisado: a worktree não pode ter mudado durante a revisão somente leitura.
        var depois = await gerenciador.ResumirAlteracoesAsync(principal.Planejada, 0, CancellationToken.None);
        if (depois.Hash != alteracoes.Hash || veredito.HashDoArtefato != alteracoes.Hash)
        {
            await ConcluirAsync(alvo, controle, roteiro, passo, new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(
                EstadoDaEtapa.Falhou, Erro: "A worktree mudou durante a revisão; o veredito não vale para o diff atual.")));
            return;
        }
        await ConcluirAsync(alvo, controle, roteiro, passo,
            new ConclusaoDoPassoDto(resultado, veredito.Veredito, alteracoes.Hash));
    }

    // Uma sessão reservada, um turno (e um lembrete opcional), encerrada sempre. Posse perdida cancela o token.
    private async Task<ResultadoDaEtapaDto> TurnoAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle,
        RoteiroDaExecucaoDto roteiro, SessionStartRequest pedido, string prompt,
        Func<CancellationToken, Task<string?>>? lembrete, CancellationToken cancellationToken)
    {
        await controle.IniciarAsync(cancellationToken);
        var observador = new ObservadorDoRoteiro(Sessoes, roteiro.IdDoUsuarioNoCanal);
        var iniciada = await Sessoes.StartAsync(pedido with { Observer = observador }, cancellationToken);
        if (!iniciada.Accepted || iniciada.Session is not { } sessao)
            return new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: iniciada.Error ?? "Sessão não iniciada.");
        try
        {
            if (await Sessoes.LedgerNumberWrittenAsync(roteiro.IdDoUsuarioNoCanal, sessao.Id, cancellationToken) is { } numero)
                await controle.VincularSessaoAsync(numero, cancellationToken);
            var desfecho = await EnviarAsync(roteiro, sessao.Id, observador, prompt, cancellationToken);
            if (desfecho.EstadoFinal == EstadoDaEtapa.Concluida && lembrete is not null &&
                await lembrete(cancellationToken) is { } texto)
                desfecho = await EnviarAsync(roteiro, sessao.Id, observador, texto, cancellationToken);
            return desfecho;
        }
        finally
        {
            await Sessoes.CloseReservedAsync(roteiro.IdDoUsuarioNoCanal, sessao.Id, CancellationToken.None);
        }
    }

    private async Task<ResultadoDaEtapaDto> EnviarAsync(RoteiroDaExecucaoDto roteiro, string sessao,
        ObservadorDoRoteiro observador, string texto, CancellationToken cancellationToken)
    {
        observador.ProximoTurno();
        var envio = await Sessoes.SubmitReservedAsync(roteiro.IdDoUsuarioNoCanal, sessao, new AgentInput(texto, []),
            cancellationToken);
        if (envio.Outcome == SubmitOutcome.Rejected) return new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: envio.Error);
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(PrazoDoTurno);
        var (desfecho, erro) = await observador.Turno.WaitAsync(prazo.Token);
        return desfecho == AgentTurnOutcome.Completed
            ? new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida)
            : new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou,
                Erro: erro ?? (desfecho == AgentTurnOutcome.Interrupted ? "Turno interrompido." : "Turno falhou."));
    }

    // Grava o desfecho e avisa a pessoa do que importa: gate aberto (com o hash), bloqueio, falha ou cancelamento.
    private async Task ConcluirAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, RoteiroDaExecucaoDto roteiro,
        PassoExecutadoDto passo, ConclusaoDoPassoDto conclusao)
    {
        var avanco = await controle.ConcluirPassoAsync(conclusao, CancellationToken.None);
        if (avanco.Estado is EstadoDaExecucao.Falhou or EstadoDaExecucao.Cancelada)
            objetivos.TryRemove(alvo.NumeroDaExecucao, out _);
        var texto = avanco switch
        {
            { GateAberto: true } =>
                $"Roteiro {roteiro.Codigo} ({roteiro.NomeDoGrupo} v{roteiro.NumeroDaVersaoDoGrupo}) aguarda sua decisão: o " +
                $"revisor aprovou o resultado da rodada {avanco.Rodada} (hash {avanco.HashDoArtefato![..12]}). Confira a " +
                $"worktree{Branch(controle)} e decida:\n/execucao aprovar {roteiro.Codigo} {avanco.HashDoArtefato[..12]}\n" +
                $"/execucao corrigir {roteiro.Codigo} {avanco.HashDoArtefato[..12]} <o que corrigir>\n" +
                $"/execucao recusar {roteiro.Codigo} [motivo]\nAprovar não faz commit, push, PR nem merge.",
            { Estado: EstadoDaExecucao.Bloqueada } =>
                $"Roteiro {roteiro.Codigo} bloqueado: {avanco.Motivo} Veja /execucao {roteiro.Codigo}; a worktree fica " +
                $"preservada até você cancelar.",
            { Estado: EstadoDaExecucao.Falhou } => $"Roteiro {roteiro.Codigo} falhou em {Nome(passo.Passo)}: {conclusao.Resultado.Erro}",
            { Estado: EstadoDaExecucao.Cancelada } => $"Roteiro {roteiro.Codigo} cancelado.",
            _ => null
        };
        if (texto is not null) await AvisarAsync(roteiro, texto);
    }

    private async Task FalharAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, RoteiroDaExecucaoDto roteiro,
        string motivo)
    {
        logger.LogWarning("Roteiro {Execucao} recusado antes do processo: {Motivo}", alvo.Codigo, motivo);
        var passo = roteiro.Etapas.Single(e => e.IdEtapa == alvo.IdEtapa);
        await ConcluirAsync(alvo, controle, roteiro, passo,
            new ConclusaoDoPassoDto(new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: motivo)));
    }

    // Planeja, reserva atomicamente, cria ou adota e valida a worktree do repositório do roteiro. Texto: recusa.
    private async Task<string?> PrepararWorktreeAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle,
        RoteiroDaExecucaoDto roteiro, ParticipanteDaExecucaoDto participante, CancellationToken cancellationToken)
    {
        WorktreePlanejada planejada;
        IReadOnlyList<WorktreeDaExecucaoDto> reservas;
        try
        {
            planejada = await gerenciador!.PlanejarAsync(
                new RepositorioCadastrado(roteiro.AliasDoRepositorio, roteiro.CheckoutDoRepositorio), alvo.NumeroDaExecucao,
                participante.Handle, cancellationToken);
            reservas = await controle.ReservarWorktreesAsync([planejada], Maquina, cancellationToken);
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
                var motivo = exception is WorktreeInvalidaException ? exception.Message
                    : $"falha ao criar a worktree ({exception.GetType().Name}).";
                await controle.FalharWorktreesAsync(reserva.Alias, motivo, CancellationToken.None);
                return $"worktree de {reserva.Alias}: {motivo}";
            }
        }
        await controle.MarcarWorktreesProntasAsync(cancellationToken);
        return await ValidarWorktreesAsync(controle.Worktrees, cancellationToken);
    }

    private async Task<string?> ValidarWorktreesAsync(IReadOnlyList<WorktreeDaExecucaoDto> worktrees,
        CancellationToken cancellationToken)
    {
        if (worktrees.Count == 0) return "a execução não tem worktree pronta.";
        foreach (var worktree in worktrees)
        {
            if (worktree.Estado != EstadoDaReservaDeWorktree.Pronta)
                return $"worktree de {worktree.Alias} não está pronta ({worktree.Estado}).";
            try
            {
                await gerenciador!.ValidarAsync(worktree.Planejada, cancellationToken);
            }
            catch (WorktreeInvalidaException exception)
            {
                return $"worktree de {worktree.Alias}: {exception.Message}";
            }
        }
        return null;
    }

    private async Task<VereditoRegistrado?> VereditoAsync(Guid idEtapa, CancellationToken cancellationToken)
    {
        await using var escopo = escopos.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<IRoteirosAppService>().ObterVereditoAsync(idEtapa, cancellationToken);
    }

    // Veredito da revisão mais recente concluída (para o corretor e para o próximo revisor).
    private async Task<VereditoRegistrado?> UltimoVereditoAsync(RoteiroDaExecucaoDto roteiro, CancellationToken cancellationToken)
    {
        var revisao = roteiro.Etapas.LastOrDefault(e => e.Passo == TipoDePassoDoRoteiro.Revisar && e.Estado == EstadoDaEtapa.Concluida);
        return revisao is null ? null : await VereditoAsync(revisao.IdEtapa, cancellationToken);
    }

    internal static string PromptDeImplementacao(RoteiroDaExecucaoDto roteiro, ParticipanteDaExecucaoDto participante,
        string objetivo) =>
        $"Tarefa do grupo {roteiro.NomeDoGrupo} para o papel {participante.Papel}:\n{objetivo}\n\n" +
        "Implemente na worktree atual (o diretório de trabalho). Você só escreve nela: o checkout principal e o " +
        "diretório Git comum são somente leitura, então não faça commit, push nem crie branches; deixe as alterações " +
        "na worktree. Ao terminar, resuma o que mudou e o que ficou de fora.";

    internal static string PromptDeCorrecao(RoteiroDaExecucaoDto roteiro, ParticipanteDaExecucaoDto participante,
        string objetivo, int rodada, VereditoRegistrado? revisao, string? instrucaoHumana)
    {
        var texto = new StringBuilder($"Rodada de correção {rodada} do grupo {roteiro.NomeDoGrupo}, papel {participante.Papel}.\n")
            .Append("Tarefa original:\n").Append(objetivo).Append("\n\n");
        if (revisao is not null)
        {
            texto.Append("Revisão anterior (").Append(Nome(revisao.Veredito)).Append("): ").Append(revisao.Resumo).Append('\n');
            foreach (var item in revisao.Itens) texto.Append("- ").Append(item).Append('\n');
        }
        if (instrucaoHumana is not null) texto.Append("\nInstrução da pessoa responsável: ").Append(instrucaoHumana).Append('\n');
        return texto.Append("\nCorrija na worktree atual, sem commit nem push, e resuma o que mudou.").ToString();
    }

    internal static string PromptDeRevisao(RoteiroDaExecucaoDto roteiro, ParticipanteDaExecucaoDto participante,
        string objetivo, int rodada, AlteracoesDaWorktreeDto alteracoes, VereditoRegistrado? anterior)
    {
        var texto = new StringBuilder($"Revisão da rodada {rodada} do grupo {roteiro.NomeDoGrupo}, papel {participante.Papel}.\n")
            .Append("Tarefa:\n").Append(objetivo).Append("\n\n")
            .Append("Você só lê: a worktree atual (diretório de trabalho) tem as alterações; o sandbox é somente leitura. ")
            .Append($"Registre o veredito uma vez, pela ferramenta entregar_resultado do dante_coordenacao: aprovado, " +
                    "requer_correcoes (um item por correção) ou bloqueado. Texto livre não registra veredito. ")
            .Append("O veredito vale para este diff (hash ").Append(alteracoes.Hash[..12]).Append(").\n");
        if (anterior is not null)
            texto.Append("\nVeredito anterior (").Append(Nome(anterior.Veredito)).Append("): ").Append(anterior.Resumo).Append('\n');
        if (alteracoes.Vazias) texto.Append("\nA worktree não tem alterações em relação à base.\n");
        else
        {
            texto.Append("\nDiff em relação à base:\n```diff\n").Append(alteracoes.Diff).Append("\n```\n");
            if (alteracoes.Cortado) texto.Append("(diff cortado: leia o restante na worktree)\n");
            if (alteracoes.NaoRastreados.Count > 0)
                texto.Append("Arquivos novos (leia-os na worktree): ").Append(string.Join(", ", alteracoes.NaoRastreados)).Append('\n');
        }
        return texto.ToString();
    }

    private static string Instrucoes(RoteiroDaExecucaoDto roteiro, ParticipanteDaExecucaoDto participante, bool escreve) =>
        $"Você é o papel {participante.Papel} do grupo {roteiro.NomeDoGrupo} v{roteiro.NumeroDaVersaoDoGrupo} na execução " +
        $"{roteiro.Codigo}." + (participante.Responsabilidades is null ? "" : $" Responsabilidades: {participante.Responsabilidades}") +
        (escreve ? " Escreva só na worktree; sem commit, push, PR ou merge."
            : " Só leitura: o resultado é o veredito registrado pela ferramenta do dante_coordenacao.");

    private static string Branch(ExecucaoSobPosse controle) =>
        controle.Worktrees.FirstOrDefault(w => w.Principal) is { } principal ? $" (branch {principal.Branch})" : "";

    private static string Nome(TipoDePassoDoRoteiro passo) => passo switch
    {
        TipoDePassoDoRoteiro.Implementar => "implementar",
        TipoDePassoDoRoteiro.Revisar => "revisar",
        TipoDePassoDoRoteiro.Corrigir => "corrigir",
        _ => "aprovação"
    };

    private static string Nome(VereditoDeRevisao veredito) => veredito switch
    {
        VereditoDeRevisao.Aprovado => "aprovado",
        VereditoDeRevisao.RequerCorrecoes => "requer correções",
        _ => "bloqueado"
    };

    private string Maquina => identidade?.Host ?? Environment.MachineName;

    private Task AvisarAsync(RoteiroDaExecucaoDto roteiro, string texto) =>
        AvisarAsync(roteiro.Canal, roteiro.IdDoUsuarioNoCanal, roteiro.Codigo, texto, CancellationToken.None);

    private async Task AvisarAsync(string canal, long usuario, string codigo, string texto, CancellationToken cancellationToken)
    {
        if (telegram is null || canal != LedgerChannel.Telegram) return;
        try
        {
            await telegram.SendMessageAsync(usuario, texto, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Falha ao avisar sobre o roteiro {Execucao} ({ErrorType}).", codigo, exception.GetType().Name);
        }
    }
}
