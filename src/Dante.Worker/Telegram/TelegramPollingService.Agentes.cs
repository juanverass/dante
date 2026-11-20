using Dante.Worker.Brain;
using Dante.Application.Agentes;
using Dante.Application.Contextos;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;
using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

// Agentes registrados no Telegram (AD-68): cadastro, /session start <handle> e /tarefa somente leitura. Sem o cadastro
// composto (banco + Orquestracao:Habilitada), os comandos novos só respondem que estão indisponíveis; mensagem comum,
// /claude, /codex, /agent set e /session start claude|codex seguem pelos caminhos de sempre.
public sealed partial class TelegramPollingService
{
    private const string AgentesIndisponiveis =
        "Agentes registrados indisponíveis: exigem o banco do D.A.N.T.E. e Orquestracao:Habilitada neste Worker.";

    private const string GruposIndisponiveis =
        "Grupos de agentes indisponíveis: exigem o banco do D.A.N.T.E. e Orquestracao:Habilitada neste Worker.";

    // /grupos e /grupo (AD-69, #308): cadastro versionado e ativação humana. A execução de um grupo entra depois.
    private async Task<string> HandleGruposCommandAsync(long userId, bool listar, string prompt,
        CancellationToken cancellationToken)
    {
        if (grupos is null) return GruposIndisponiveis;
        if (listar) return await grupos.ListarAsync(prompt, cancellationToken);
        if (prompt.Length == 0) return await grupos.ListarAsync("", cancellationToken) + "\n\n" + TelegramGrupos.Uso;
        return await grupos.AtenderAsync(userId, prompt, cancellationToken);
    }

    // /grupo <nome> @alias <tarefa> (#308): resolve o grupo (versão ativa, participantes válidos) e o repositório
    // explícito e enfileira o roteiro pela fila durável; nada é inferido do texto. Implementar escreve só numa worktree
    // com agente Codex confinado; revisar lê em sandbox somente leitura; o fim é a decisão humana no /execucao.
    // Grupo no modo coordenador (#310, AD-75): a mesma forma cria a Coordenada; o coordenador propõe o plano somente
    // leitura e nada é delegado antes da aprovação humana dele.
    private async Task HandleRoteiroAsync(TelegramMessage message, string nome, string resto, CancellationToken cancellationToken)
    {
        var (userId, chatId) = (message.From!.Id, message.Chat.Id);
        if (grupos is null || filaDeRoteiros is null)
        {
            await SendReplyAsync(chatId, grupos is null ? GruposIndisponiveis
                : "A execução de grupos depende da fila durável, que não está habilitada neste Worker; nada foi executado.",
                cancellationToken);
            return;
        }
        var resolucao = await grupos.ResolverAsync(nome, cancellationToken);
        if (!resolucao.Resolvido)
        {
            await SendReplyAsync(chatId, resolucao.Erro!, cancellationToken);
            return;
        }
        var resolved = resolver.Resolve(userId, AgentKind.Codex, resto);
        if (!resolved.Succeeded)
        {
            await SendReplyAsync(chatId, resolved.Failure == ContextResolutionFailure.EmptyPrompt
                ? "Uso: /grupo <nome> @alias <tarefa>" : resolved.Error!, cancellationToken);
            return;
        }
        var chave = message.MessageId > 0 ? $"telegram:{chatId}:{message.MessageId}" : $"telegram:{chatId}:sem-id:{Guid.NewGuid():N}";
        if (resolucao.Versao!.Configuracao!.Modo == Dante.Domain.Grupos.ModoDeCoordenacao.Coordenador)
        {
            if (filaDeCoordenacoes is null)
            {
                await SendReplyAsync(chatId, "A coordenação de grupos não está habilitada neste Worker; nada foi executado.",
                    cancellationToken);
                return;
            }
            var coordenacao = await filaDeCoordenacoes.CriarAsync(resolucao, resolved.Prompt, resolved.Context!, userId,
                chave, jobs.LastNumber, cancellationToken);
            await SendReplyAsync(chatId, coordenacao.Codigo is { } codigoDaCoordenacao
                ? $"Coordenação {codigoDaCoordenacao} enfileirada: grupo {resolucao.Nome} v{resolucao.Versao.Numero}, " +
                  $"coordenador {resolucao.Versao.Configuracao.PapelDoCoordenador}, em {resolved.Context!.Descricao}. O " +
                  "coordenador propõe um plano somente leitura e você o aprova antes de qualquer delegação; acompanhe com " +
                  $"/execucao {codigoDaCoordenacao}. Nenhum agente escreve, faz commit, push, PR ou merge."
                : coordenacao.Recusa!, cancellationToken);
            return;
        }
        var criada = await filaDeRoteiros.CriarAsync(resolucao, resolved.Prompt, resolved.Context!, userId, chave,
            jobs.LastNumber, cancellationToken);
        await SendReplyAsync(chatId, criada.Codigo is { } codigo
            ? $"Roteiro {codigo} enfileirado: grupo {resolucao.Nome} v{resolucao.Versao!.Numero} " +
              $"({string.Join(" → ", resolucao.Versao.Configuracao!.Roteiro.Select(p => p.Papel ?? "aprovação humana"))}) " +
              $"em {resolved.Context!.Descricao}. Acompanhe com /execucao {codigo}; você decide no fim. " +
              "Nenhum agente faz commit, push, PR ou merge."
            : criada.Recusa!, cancellationToken);
    }

    private async Task<string> HandleAgentesCommandAsync(long userId, bool listar, string prompt,
        CancellationToken cancellationToken)
    {
        if (agentes is null) return AgentesIndisponiveis;
        if (listar) return await agentes.ListarAsync(prompt, cancellationToken);
        if (prompt.Length == 0) return await agentes.ListarAsync("", cancellationToken) + "\n\n" + TelegramAgentes.Uso;
        return await agentes.AtenderAsync(userId, prompt, handle =>
        {
            var vivas = sessions?.ListLiveOfRegisteredAgent(handle) ?? [];
            // Só os códigos das sessões de quem pediu; as de outros usuários aparecem só na contagem (AD-20).
            var proprias = vivas.Where(sessao => sessao.OwnerUserId == userId).Select(sessao => sessao.Id).ToArray();
            return (proprias.Length, vivas.Count - proprias.Length, proprias);
        }, cancellationToken);
    }

    // /session start <handle>: a versão atual do agente fica congelada na sessão. Agente inexistente, desativado ou
    // arquivado recusa sem cair para outro; modo explícito só restringe (nunca passa do máximo da versão); modelo e
    // esforço vêm da versão e são conferidos de novo no catálogo antes do processo.
    private async Task StartRegisteredSessionAsync(TelegramMessage message, string handle, bool builtInToo,
        string? alias, AgentPermissionProfile? requestedProfile, string? model, string? effort,
        CancellationToken cancellationToken)
    {
        var (userId, chatId) = (message.From!.Id, message.Chat.Id);
        if (builtInToo)
        {
            await SendReplyAsync(chatId, "Escolha um agente só: claude, codex ou um handle registrado.", cancellationToken);
            return;
        }
        if (model is not null || effort is not null)
        {
            await SendReplyAsync(chatId, $"Modelo e esforço do agente {handle} vêm da versão dele; para mudar, use " +
                $"/agente editar {handle} model=… effort=…", cancellationToken);
            return;
        }
        var resolucao = await agentes!.ResolverAsync(handle, cancellationToken);
        if (!resolucao.Resolvido)
        {
            await SendReplyAsync(chatId, resolucao.Erro!, cancellationToken);
            return;
        }
        var versao = resolucao.Versao!;
        var configuracao = versao.Configuracao;
        var registered = new AgenteDaSessao(resolucao.Handle, versao);
        var profile = requestedProfile ?? configuracao.PerfilPadrao;
        if (!configuracao.PermitePerfil(profile))
        {
            await SendReplyAsync(chatId, $"O modo {AgentSessionModes.Name(profile)} passa do máximo do agente " +
                $"{registered.Descricao} ({AgentSessionModes.Name(configuracao.PerfilMaximo)}).", cancellationToken);
            return;
        }
        var resolved = resolver.ResolveContext(userId, configuracao.Driver, alias);
        if (!resolved.Succeeded)
        {
            await SendReplyAsync(chatId, resolved.Error!, cancellationToken);
            return;
        }
        var (agent, context, environment) = (configuracao.Driver, resolved.Context!, resolved.Environment);
        if (!MontagemDaTarefa.ReposPermitidos(configuracao, context))
        {
            await SendReplyAsync(chatId, MontagemDaTarefa.RecusaDeRepositorio(registered.Handle, configuracao, context),
                cancellationToken);
            return;
        }
        var modelSelection = await ResolveModelAsync(userId, agent, configuracao.Modelo ?? "default", chatId,
            cancellationToken, configuracao.Esforco ?? "default");
        if (modelSelection is null) return;
        var started = await sessions!.StartAsync(new SessionStartRequest(userId, agent, context, environment?.Values,
            profile, modelSelection, message.ParaFerramentas(), registered), cancellationToken);
        if (started.Accepted)
        {
            delivery.RegisterSession(started.Session!.Id, userId, chatId, environment?.HasSecrets == true);
            SyncActiveSession(userId);
            await SendReplyAsync(chatId,
                $"Sessão {started.Session.Id} iniciada com o agente {registered.Descricao} ({agent}, {context.Descricao}), " +
                $"modo {AgentSessionModes.Label(profile)}{ModelSuffix(modelSelection)}. Envie uma mensagem para iniciar o turno.",
                cancellationToken);
        }
        else await SendReplyAsync(chatId,
            environment?.HasSecrets == true && started.Session is not null ? "Falha ao iniciar sessão." :
                started.Error ?? "Falha ao iniciar sessão.", cancellationToken);
    }

    // /tarefa <handle> [@alias] <texto> (AD-68): valida agente, contexto e concessões e monta a execução Tarefa
    // somente leitura (perfil plan, nenhuma etapa de escrita, versão congelada). /tarefa <handle> @alias escrever
    // <texto> (#307) é o único pedido de escrita: exige o @alias explícito e é validado (capacidade, perfil padrão,
    // driver com confinamento) antes de enfileirar. O despacho durável depende da fila da #300 e passa só por
    // DespacharTarefaAsync.
    private async Task HandleTarefaCommandAsync(TelegramMessage message, string prompt, CancellationToken cancellationToken)
    {
        const string usage = "Uso: /tarefa <handle> [@alias] <pedido> | /tarefa <handle> @alias escrever <pedido>";
        var (userId, chatId) = (message.From!.Id, message.Chat.Id);
        if (agentes is null)
        {
            await SendReplyAsync(chatId, AgentesIndisponiveis, cancellationToken);
            return;
        }
        var separator = prompt.IndexOfAny([' ', '\t', '\r', '\n']);
        var handle = separator < 0 ? prompt : prompt[..separator];
        var rest = separator < 0 ? string.Empty : prompt[(separator + 1)..];
        if (handle.Length == 0)
        {
            await SendReplyAsync(chatId, usage, cancellationToken);
            return;
        }
        if (AgentesEmbutidos.TryObter(handle, out _))
        {
            await SendReplyAsync(chatId, "Tarefas usam um agente registrado (veja /agentes); para um pedido avulso use " +
                "/claude ou /codex.", cancellationToken);
            return;
        }
        var resolucao = await agentes.ResolverAsync(handle, cancellationToken);
        if (!resolucao.Resolvido)
        {
            await SendReplyAsync(chatId, resolucao.Erro!, cancellationToken);
            return;
        }
        var configuracao = resolucao.Versao!.Configuracao;
        var resolved = resolver.Resolve(userId, configuracao.Driver, rest);
        if (!resolved.Succeeded)
        {
            await SendReplyAsync(chatId, resolved.Failure == ContextResolutionFailure.EmptyPrompt ? usage : resolved.Error!,
                cancellationToken);
            return;
        }
        // O marcador vem logo depois do @alias explícito; sem alias, "escrever" é só o começo de um pedido de leitura
        // recusado com a orientação, para nunca virar escrita no repositório ativo por engano.
        var (escrita, pedido) = PedidoDeEscrita(resolved.Prompt);
        if (escrita && !rest.TrimStart().StartsWith('@'))
        {
            await SendReplyAsync(chatId, "Tarefa de escrita exige o repositório explícito: /tarefa <handle> @alias escrever <pedido>.",
                cancellationToken);
            return;
        }
        if (escrita && pedido.Length == 0)
        {
            await SendReplyAsync(chatId, usage, cancellationToken);
            return;
        }
        PlanoDaTarefa plano;
        try
        {
            plano = escrita
                ? MontagemDaTarefa.MontarEscrita(resolucao.Handle, resolucao.Versao, resolved.Context!, pedido)
                : MontagemDaTarefa.Montar(resolucao.Handle, resolucao.Versao, resolved.Context!, resolved.Prompt);
        }
        catch (ArgumentException exception)
        {
            await SendReplyAsync(chatId, exception.Message, cancellationToken);
            return;
        }
        // Modelo e esforço da versão conferidos no catálogo agora, como em qualquer início (AD-25/26).
        if (await ResolveModelAsync(userId, configuracao.Driver, configuracao.Modelo ?? "default", chatId,
                cancellationToken, configuracao.Esforco ?? "default") is null) return;
        await DespacharTarefaAsync(plano, userId, chatId, message.MessageId, resolved.Environment, cancellationToken);
    }

    private static (bool Escrita, string Pedido) PedidoDeEscrita(string prompt)
    {
        var texto = prompt.TrimStart();
        var marcador = MontagemDaTarefa.MarcadorDeEscrita;
        if (!texto.StartsWith(marcador, StringComparison.Ordinal) ||
            (texto.Length > marcador.Length && !char.IsWhiteSpace(texto[marcador.Length])))
            return (false, prompt);
        return (true, texto[marcador.Length..].Trim());
    }

    private static string Aviso(PlanoDaTarefa plano) =>
        plano.Escrita ? MontagemDaTarefa.AvisoDeEscrita : MontagemDaTarefa.AvisoDeLeitura;

    // PONTO ÚNICO DE INTEGRAÇÃO COM A FILA DURÁVEL (AD-67). A tarefa montada vira, numa só transação, a execução
    // Tarefa, sua etapa somente leitura, o evento de criação e o trabalho que a despacha (deduplicado pela mensagem do
    // Telegram); só depois o usuário recebe a confirmação. A execução acontece quando o processador reivindica o
    // trabalho: ExecutorDeTarefasDaFila confere guarda, versão congelada, agente e catálogo e chama RunTarefaAsync.
    // Sem a fila composta, nada é criado nem executado: não existe caminho paralelo.
    private async Task DespacharTarefaAsync(PlanoDaTarefa plano, long userId, long chatId, long messageId,
        ResolvedRepositoryEnvironment? ambiente, CancellationToken cancellationToken)
    {
        var descricao = $"agente {plano.Handle} v{plano.Versao.Numero} ({plano.Contexto.Descricao})";
        if (filaDeTarefas is null)
        {
            await SendReplyAsync(chatId, $"Tarefa para o {descricao} validada. " + Aviso(plano) +
                "\nA execução durável de tarefas não está habilitada neste Worker (depende da fila durável); " +
                "nada foi executado.", cancellationToken);
            return;
        }
        // Telegram always sends message_id; without one there is nothing to deduplicate by, and the task gets its own key.
        var chave = messageId > 0 ? $"telegram:{chatId}:{messageId}" : $"telegram:{chatId}:sem-id:{Guid.NewGuid():N}";
        var criada = await filaDeTarefas.CriarAsync(plano, userId, chave, cancellationToken);
        if (criada is not { Numero: { } numero, Codigo: { } codigo })
        {
            await SendReplyAsync(chatId, criada.Recusa!, cancellationToken);
            return;
        }
        // Until released, the processor waits; any exit without release ends the task as failed, never runs it.
        var liberada = false;
        try
        {
            await SendReplyAsync(chatId, $"Tarefa {codigo} enfileirada: {descricao}. {Aviso(plano)}",
                cancellationToken);
            filaDeTarefas.Liberar(numero, (controle, token) =>
                RunTarefaAsync(codigo, plano, ambiente, userId, chatId, controle, token));
            liberada = true;
        }
        finally
        {
            if (!liberada) filaDeTarefas.Abortar(numero, "Falha ao confirmar o recebimento da tarefa.");
        }
    }

    // Uma etapa da Tarefa: a etapa vai a EmExecucao sob posse, a sessão reservada (perfil plan, versão congelada,
    // concessões da versão) roda um turno com o pedido fora de qualquer transação e é encerrada; o resultado é gravado
    // sob posse e só então entregue. Posse perdida ou encerramento cancelam o token: nada é gravado nem respondido.
    private async Task RunTarefaAsync(string codigo, PlanoDaTarefa plano, ResolvedRepositoryEnvironment? ambiente,
        long userId, long chatId, ExecucaoSobPosse controle, CancellationToken cancellationToken)
    {
        await controle.IniciarAsync(cancellationToken);
        ResultadoDaEtapaDto resultado;
        string? saida = null;
        if (sessions is null)
            resultado = new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: "Sessões de agente indisponíveis neste Worker.");
        else
        {
            var observador = new ObservadorDaTarefa(sessions, userId);
            // Escrita (#307): só as worktrees prontas da execução, nunca o checkout principal.
            // Leitura com a concessão coordenacao (#309): a sessão delega pelo dante_coordenacao, sob esta posse.
            var iniciada = await sessions.StartAsync(SolicitacaoDaTarefa.Para(plano, userId, ambiente?.Values,
                plano.Escrita ? controle.Worktrees : null, controle) with
            {
                Observer = observador
            }, cancellationToken);
            if (!iniciada.Accepted || iniciada.Session is not { } sessao)
                resultado = new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: iniciada.Error ?? "Sessão não iniciada.");
            else
            {
                try
                {
                    // A etapa aponta para a sessão que a executa (#305), sob posse e antes do turno: posse perdida lança
                    // aqui, nada é enviado ao agente e a sessão é encerrada abaixo.
                    await VincularSessaoAsync(codigo, controle, userId, sessao.Id, cancellationToken);
                    var envio = await sessions.SubmitReservedAsync(userId, sessao.Id, new AgentInput(plano.Pedido, []),
                        cancellationToken);
                    if (envio.Outcome == SubmitOutcome.Rejected)
                        resultado = new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: envio.Error);
                    else
                    {
                        var (desfecho, erro) = await observador.Turno.WaitAsync(cancellationToken);
                        saida = observador.UltimaMensagem;
                        resultado = desfecho == AgentTurnOutcome.Completed
                            ? new ResultadoDaEtapaDto(EstadoDaEtapa.Concluida)
                            : new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou,
                                Erro: erro ?? (desfecho == AgentTurnOutcome.Interrupted ? "Turno interrompido." : "Turno falhou."));
                    }
                }
                finally
                {
                    await sessions.CloseReservedAsync(userId, sessao.Id, CancellationToken.None);
                }
            }
        }
        // Gravado antes de qualquer resposta; posse perdida lança e nada é entregue (AD-67).
        await controle.ConcluirAsync(resultado, CancellationToken.None);
        // O dono pediu cancelamento antes da conclusão (#305): a gravação descartou o resultado; um único aviso, e a
        // saída do agente não é entregue.
        if (controle.EstadoFinal == EstadoDaEtapa.Cancelada && resultado.EstadoFinal != EstadoDaEtapa.Cancelada)
        {
            try
            {
                await botApi.SendMessageAsync(chatId, ExecutorDeTarefasDaFila.Cancelada(codigo, null), CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Falha ao avisar o cancelamento da tarefa {Execucao} ({ErrorType}).", codigo,
                    exception.GetType().Name);
            }
            return;
        }
        var cabecalho = $"Tarefa {codigo} ({plano.Handle} v{plano.Versao.Numero}, {plano.Contexto.Descricao})";
        // Onde ficaram as alterações: a branch de cada worktree (removida depois se ficou limpa, preservada se não).
        if (plano.Escrita && controle.Worktrees.Count > 0)
            cabecalho += $" [worktree: {string.Join(", ", controle.Worktrees.Select(w => $"{w.Alias} {w.Branch}"))}]";
        var resposta = resultado.EstadoFinal == EstadoDaEtapa.Concluida
            ? ambiente?.HasSecrets == true
                ? $"{cabecalho} concluída. Saída omitida para proteger segredos do ambiente."
                : $"{cabecalho} concluída.\n\n{OutputOrFallback(saida ?? "")}"
            : ambiente?.HasSecrets == true
                ? $"{cabecalho} falhou. Detalhes omitidos para proteger segredos do ambiente."
                : $"{cabecalho} falhou.\n\n{resultado.Erro}";
        try
        {
            await delivery.DeliverJobAsync(codigo, userId, chatId, resposta, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Falha ao informar o resultado da tarefa {Execucao} ({ErrorType}).", codigo,
                exception.GetType().Name);
        }
    }

    // Vínculo etapa → sessão (#305). Posse perdida e cancelamento propagam. Sem espelho no ledger (número do contador
    // local) ou com a gravação do espelho falhada, a tarefa segue sem o vínculo: o S… é informativo, e a posse decide.
    private async Task VincularSessaoAsync(string codigo, ExecucaoSobPosse controle, long userId, string idSessao,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await sessions!.LedgerNumberWrittenAsync(userId, idSessao, cancellationToken) is not { } numero)
            {
                logger.LogWarning("Sessão {SessionId} da tarefa {Execucao} sem registro no ledger; etapa sem vínculo.",
                    idSessao, codigo);
                return;
            }
            await controle.VincularSessaoAsync(numero, cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception is not PosseDoTrabalhoPerdidaException)
        {
            logger.LogWarning("Sessão {SessionId} não vinculada à tarefa {Execucao} ({ErrorType}).", idSessao, codigo,
                exception.GetType().Name);
        }
    }
}
