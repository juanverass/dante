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
    // somente leitura (perfil plan, nenhuma etapa de escrita, versão congelada). O despacho durável depende da fila
    // da #300 e passa só por DespacharTarefaAsync.
    private async Task HandleTarefaCommandAsync(TelegramMessage message, string prompt, CancellationToken cancellationToken)
    {
        const string usage = "Uso: /tarefa <handle> [@alias] <pedido>";
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
        PlanoDaTarefa plano;
        try
        {
            plano = MontagemDaTarefa.Montar(resolucao.Handle, resolucao.Versao, resolved.Context!, resolved.Prompt);
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
            await SendReplyAsync(chatId, $"Tarefa para o {descricao} validada. " + MontagemDaTarefa.AvisoDeLeitura +
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
            await SendReplyAsync(chatId, $"Tarefa {codigo} enfileirada: {descricao}. {MontagemDaTarefa.AvisoDeLeitura}",
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
            var iniciada = await sessions.StartAsync(SolicitacaoDaTarefa.Para(plano, userId, ambiente?.Values) with
            {
                Observer = observador
            }, cancellationToken);
            if (!iniciada.Accepted || iniciada.Session is not { } sessao)
                resultado = new ResultadoDaEtapaDto(EstadoDaEtapa.Falhou, Erro: iniciada.Error ?? "Sessão não iniciada.");
            else
            {
                try
                {
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
        var cabecalho = $"Tarefa {codigo} ({plano.Handle} v{plano.Versao.Numero}, {plano.Contexto.Descricao})";
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
}
