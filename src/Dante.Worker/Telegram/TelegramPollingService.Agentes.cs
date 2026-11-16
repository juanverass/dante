using Dante.Worker.Brain;
using Dante.Application.Agentes;
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
        await SendReplyAsync(chatId, await DespacharTarefaAsync(plano, userId, chatId, cancellationToken),
            cancellationToken);
    }

    // PONTO ÚNICO DE INTEGRAÇÃO COM A FILA DURÁVEL (#300). Aqui a tarefa montada deve virar, numa só transação, a
    // execução Tarefa e sua etapa (MontagemDaTarefa.CriarExecucao, com número de seq_numero_da_execucao) e o trabalho
    // que a despacha pela IFilaDeTrabalho; o executor abre a sessão com SolicitacaoDaTarefa.Para(plano, ...) e chama
    // GuardaDeEscrita.Exigir antes de iniciar cada etapa. Até lá, nada é criado nem executado: não existe caminho
    // paralelo à fila.
    private static Task<string> DespacharTarefaAsync(PlanoDaTarefa plano, long userId, long chatId,
        CancellationToken cancellationToken)
    {
        _ = (userId, chatId, cancellationToken);
        return Task.FromResult(
            $"Tarefa para o agente {plano.Handle} v{plano.Versao.Numero} ({plano.Contexto.Descricao}) validada. " +
            MontagemDaTarefa.AvisoDeLeitura + "\n" +
            "A execução durável de tarefas ainda não está habilitada neste Worker (depende da fila durável); " +
            "nada foi executado.");
    }
}
