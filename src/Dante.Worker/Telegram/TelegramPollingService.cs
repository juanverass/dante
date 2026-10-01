using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Repositories;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;
using System.Text;
using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Worker.Telegram;

public sealed class TelegramPollingService(
    ITelegramBotApi botApi,
    IOptions<TelegramOptions> options,
    TelegramUserAuthorizer authorizer,
    ICodexRunner codexRunner,
    IClaudeRunner claudeRunner,
    JobRegistry jobs,
    ILogger<TelegramPollingService> logger,
    RepositoryRegistry? repositories = null,
    GeneralWorkspace? generalWorkspace = null,
    AssistantSettingsStore? settings = null,
    SessionRegistry? sessions = null,
    TelegramDeliveryService? delivery = null) : BackgroundService
{
    private const int MaxMessageLength = 4000;
    private readonly object runningGate = new();
    private readonly HashSet<Task> runningJobs = [];
    private readonly Dictionary<long, AgentPermissionProfile> selectedProfiles = [];
    private readonly GeneralWorkspace generalWorkspace = generalWorkspace ?? new GeneralWorkspace();
    private readonly TelegramDeliveryService delivery = delivery ??
        new TelegramDeliveryService(botApi, NullLogger<TelegramDeliveryService>.Instance);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Task[] pending;
        lock (runningGate)
        {
            pending = runningJobs.ToArray();
        }

        await Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BotToken))
        {
            logger.LogWarning("Telegram__BotToken não configurado; polling desativado.");
            return;
        }

        long offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await botApi.GetUpdatesAsync(offset, stoppingToken);
                foreach (var update in updates)
                {
                    // Advance before dispatch so a failed command is not executed again on the next poll.
                    offset = Math.Max(offset, update.UpdateId + 1);
                    if (update.Message is { Text: not null } message
                        && authorizer.IsAuthorized(message.From))
                    {
                        await HandleMessageAsync(message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Exception messages from HTTP clients can contain the token-bearing request URL.
                logger.LogWarning("Falha no polling do Telegram ({ErrorType}); tentando novamente.",
                    exception.GetType().Name);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleMessageAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        var text = message.Text!.Trim();
        if (string.Equals(text, "/ping", StringComparison.OrdinalIgnoreCase))
        {
            await SendReplyAsync(message.Chat.Id, "pong", cancellationToken);
            return;
        }

        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var command = separator < 0 ? text : text[..separator];
        var prompt = separator < 0 ? string.Empty : text[(separator + 1)..].Trim();
        if (string.Equals(command, "/repos", StringComparison.OrdinalIgnoreCase))
        {
            var registered = repositories?.List() ?? [];
            await SendLongMessageAsync(message.Chat.Id,
                registered.Count == 0 ? "Nenhum repositório cadastrado." :
                    string.Join('\n', registered.Select(FormatRepository)), cancellationToken);
            return;
        }

        if (string.Equals(command, "/repo", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRepositoryCommandAsync(message.Chat.Id, prompt, cancellationToken);
            return;
        }

        if (string.Equals(command, "/agent", StringComparison.OrdinalIgnoreCase))
        {
            await SendReplyAsync(message.Chat.Id, HandleAgentCommand(message.From!.Id, prompt), cancellationToken);
            return;
        }

        if (string.Equals(command, "/use", StringComparison.OrdinalIgnoreCase))
        {
            await SendReplyAsync(message.Chat.Id, HandleUseCommand(message.From!.Id, prompt),
                cancellationToken);
            return;
        }

        if (string.Equals(command, "/status", StringComparison.OrdinalIgnoreCase))
        {
            var visible = jobs.GetVisible();
            var response = visible.Count == 0
                ? "Nenhum job registrado."
                : string.Join('\n', visible.Select(FormatJob));
            // Sessions are listed apart from jobs (AD-16), and only the requesting user's own.
            var ownSessions = sessions?.List(message.From!.Id) ?? [];
            if (ownSessions.Count > 0)
            {
                response += "\n\nSessões:\n" + string.Join('\n', ownSessions.Select(session =>
                    FormatSession(session) + (session.Error is null || delivery.HidesOutput(session.Id)
                        ? string.Empty : $" | erro: {session.Error}")));
            }
            var deliveries = visible.Select(job => delivery.Get(job.Id, message.From!.Id))
                .Concat(ownSessions.Select(session => delivery.Get(session.Id, message.From!.Id)))
                .Where(snapshot => snapshot is not null).ToArray();
            if (deliveries.Length > 0)
                response += "\n\nEntregas Telegram:\n" + string.Join('\n', deliveries.Select(snapshot =>
                    $"{snapshot!.Id}: {snapshot.State} ({snapshot.DeliveredChunks}/{snapshot.TotalChunks} partes)"));
            await SendLongMessageAsync(message.Chat.Id, response, cancellationToken);
            return;
        }

        if (string.Equals(command, "/session", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSessionCommandAsync(message, prompt, cancellationToken);
            return;
        }

        if (string.Equals(command, "/permissions", StringComparison.OrdinalIgnoreCase))
        {
            await SendReplyAsync(message.Chat.Id, HandlePermissionsCommand(message.From!.Id, prompt),
                cancellationToken);
            return;
        }

        if (command.Equals("/approve", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("/approve-session", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("/deny", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("/input", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRequestCommandAsync(message, command, prompt, cancellationToken);
            return;
        }

        if (string.Equals(command, "/steer", StringComparison.OrdinalIgnoreCase))
        {
            await SubmitToSessionAsync(message, prompt, MessageDelivery.Steer, cancellationToken);
            return;
        }

        if (string.Equals(command, "/resend", StringComparison.OrdinalIgnoreCase))
        {
            if (prompt.Length == 0)
            {
                await SendReplyAsync(message.Chat.Id, "Uso: /resend <jobId|sessionId[/turnId]>", cancellationToken);
                return;
            }
            var recovered = await delivery.RetryAsync(prompt, message.From!.Id, message.Chat.Id, cancellationToken);
            await SendReplyAsync(message.Chat.Id, recovered is null
                ? "Saída recente não encontrada para reenvio."
                : $"Entrega {recovered.Id}: {recovered.State} ({recovered.DeliveredChunks}/{recovered.TotalChunks} partes).",
                cancellationToken);
            return;
        }

        if (string.Equals(command, "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            if (prompt.Length == 0 || prompt.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
            {
                await SendReplyAsync(message.Chat.Id, "Uso: /cancel <jobId>", cancellationToken);
                return;
            }

            var response = jobs.TryCancel(prompt, out var cancelled)
                ? $"Cancelamento solicitado para {cancelled!.Id} ({cancelled.Context.Label})."
                : $"Job {prompt} não encontrado ou já encerrado.";
            await SendReplyAsync(message.Chat.Id, response, cancellationToken);
            return;
        }

        var isCodex = string.Equals(command, "/codex", StringComparison.OrdinalIgnoreCase);
        var isClaude = string.Equals(command, "/claude", StringComparison.OrdinalIgnoreCase);
        if (!isCodex && !isClaude)
        {
            if (text.StartsWith('/'))
            {
                // Unknown commands are never forwarded to the default agent as prompts.
                await SendReplyAsync(message.Chat.Id, $"Comando desconhecido: {command}.", cancellationToken);
                return;
            }

            if (text.Length == 0) return;
            if (sessions is not null)
            {
                await ConverseAsync(message, text, cancellationToken);
                return;
            }
            // Without a session registry, plain messages fall back to one-shot jobs.
            isCodex = (settings?.Current ?? AssistantSettings.Default).DefaultAgent == AgentKind.Codex;
            prompt = text;
        }

        var agent = isCodex ? "Codex" : "Claude";
        if (prompt.Length == 0)
        {
            await SendReplyAsync(message.Chat.Id, $"Uso: /{agent.ToLowerInvariant()} [@alias] <prompt>",
                cancellationToken);
            return;
        }

        var workingDirectory = generalWorkspace.Path;
        var generalMode = true;
        string? repositoryAlias = null;
        ResolvedRepositoryEnvironment? repositoryEnvironment = null;
        RepositoryDefinition? repository = null;
        var firstSpace = prompt.IndexOfAny([' ', '\t', '\r', '\n']);
        var firstArgument = firstSpace < 0 ? prompt : prompt[..firstSpace];
        if (firstArgument.StartsWith('@'))
        {
            try { repository = repositories?.Get(firstArgument); }
            catch (ArgumentException)
            {
                await SendReplyAsync(message.Chat.Id, "Alias inválido.", cancellationToken);
                return;
            }

            if (repository is null)
            {
                await SendReplyAsync(message.Chat.Id, $"Repositório {firstArgument} não cadastrado.",
                    cancellationToken);
                return;
            }
            prompt = firstSpace < 0 ? string.Empty : prompt[(firstSpace + 1)..].Trim();
            if (prompt.Length == 0)
            {
                await SendReplyAsync(message.Chat.Id, $"Uso: /{agent.ToLowerInvariant()} @alias <prompt>",
                    cancellationToken);
                return;
            }
        }
        else if (settings?.GetActiveRepository(message.From!.Id) is { } activeAlias)
        {
            // A stale active repository requires a new selection instead of silently falling back to General.
            repository = repositories?.Get(activeAlias);
            if (repository is null)
            {
                await SendReplyAsync(message.Chat.Id,
                    $"O repositório ativo {activeAlias} não está mais cadastrado. Use /use @alias ou /use general.",
                    cancellationToken);
                return;
            }
        }

        if (repository is not null)
        {
            workingDirectory = repository.Path;
            generalMode = false;
            repositoryAlias = repository.Alias;
            try { repositoryEnvironment = repositories!.ResolveEnvironment(repository.Alias); }
            catch (InvalidOperationException exception)
            {
                await SendReplyAsync(message.Chat.Id, exception.Message, cancellationToken);
                return;
            }
        }

        if (generalMode && repositories?.List().Any(repository =>
                IsWithin(workingDirectory, repository.Path) ||
                IsWithin(repository.Path, workingDirectory)) == true)
        {
            await SendReplyAsync(message.Chat.Id,
                "O workspace geral coincide com um repositório cadastrado; configure DANTE_GENERAL_WORKSPACE fora dos projetos.",
                cancellationToken);
            return;
        }

        var context = generalMode ? JobExecutionContext.General(workingDirectory) :
            JobExecutionContext.Repository(repositoryAlias!, workingDirectory);
        var (job, jobToken) = jobs.Create(agent, context, cancellationToken);
        try
        {
            await SendReplyAsync(message.Chat.Id,
                $"{agent} iniciado. Job ID: {job.Id} ({context.Label}).", cancellationToken);
        }
        catch
        {
            jobs.Complete(job.Id, AgentProcessStatus.Failed, errorMessage: "Falha ao confirmar o início.");
            throw;
        }

        var task = RunJobAsync(job.Id, agent, isCodex, prompt, context,
            repositoryEnvironment, message.From!.Id, message.Chat.Id, jobToken,
            cancellationToken);
        lock (runningGate)
        {
            runningJobs.Add(task);
        }

        _ = task.ContinueWith(completed =>
        {
            lock (runningGate)
            {
                runningJobs.Remove(completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleRepositoryCommandAsync(long chatId, string prompt, CancellationToken cancellationToken)
    {
        const string usage = "Uso: /repo add @alias <path> [owner/repo] | show @alias | remove @alias";
        var parts = SplitCommandArguments(prompt);
        if (parts is null)
        {
            await SendReplyAsync(chatId, "Aspas não fechadas no comando /repo.", cancellationToken);
            return;
        }
        if (repositories is null || parts.Length < 2)
        {
            await SendReplyAsync(chatId, usage, cancellationToken);
            return;
        }

        if (parts[0].Equals("env", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRepositoryEnvironmentCommandAsync(chatId, parts, cancellationToken);
            return;
        }

        string response;
        try
        {
            if (parts[0].Equals("add", StringComparison.OrdinalIgnoreCase) && parts.Length is 3 or 4)
            {
                response = "Repositório cadastrado: " + FormatRepository(
                    repositories.Add(parts[1], parts[2], parts.Length == 4 ? parts[3] : null));
            }
            else if (parts[0].Equals("show", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
            {
                var found = repositories.Get(parts[1]);
                response = found is null ? "Repositório não cadastrado." : FormatRepository(found);
            }
            else if (parts[0].Equals("remove", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
            {
                var removed = repositories.Remove(parts[1]);
                if (removed) ClearActiveRepository(parts[1]);
                response = removed ? "Repositório removido." : "Repositório não cadastrado.";
            }
            else response = usage;
        }
        catch (ArgumentException exception)
        {
            response = exception.Message;
        }
        catch (IOException)
        {
            response = "Não foi possível salvar o catálogo de repositórios.";
        }

        await SendReplyAsync(chatId, response, cancellationToken);
    }

    private async Task HandleSessionCommandAsync(TelegramMessage message, string prompt,
        CancellationToken cancellationToken)
    {
        const string usage = "Uso: /session start [claude|codex] [@alias] [manual|auto|plan] | list | select <id|none> | stop [id] | close [id]";
        if (sessions is null)
        {
            await SendReplyAsync(message.Chat.Id, "Sessões indisponíveis.", cancellationToken);
            return;
        }
        var parts = prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var userId = message.From!.Id;
        if (parts.Length == 0 || parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var own = sessions.List(userId);
            await SendLongMessageAsync(message.Chat.Id, own.Count == 0 ? "Nenhuma sessão registrada." :
                string.Join('\n', own.Select(FormatSession)), cancellationToken);
            return;
        }

        if (parts[0].Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            var agent = (settings?.Current ?? AssistantSettings.Default).DefaultAgent;
            var profile = selectedProfiles.GetValueOrDefault(userId, AgentPermissionProfile.Manual);
            string? alias = null;
            var profileSpecified = false;
            for (var index = 1; index < parts.Length; index++)
            {
                if (parts[index].Equals("claude", StringComparison.OrdinalIgnoreCase)) agent = AgentKind.Claude;
                else if (parts[index].Equals("codex", StringComparison.OrdinalIgnoreCase)) agent = AgentKind.Codex;
                else if (parts[index].StartsWith('@') && alias is null) alias = parts[index];
                else if (TryParseProfile(parts[index], out var requested) && !profileSpecified)
                {
                    profile = requested;
                    profileSpecified = true;
                }
                else
                {
                    await SendReplyAsync(message.Chat.Id, usage, cancellationToken);
                    return;
                }
            }
            var resolved = await ResolveSessionContextAsync(userId, alias, message.Chat.Id, cancellationToken);
            if (resolved is null) return;
            var (context, environment) = resolved.Value;
            var started = await sessions.StartAsync(new SessionStartRequest(userId, agent, context,
                environment?.Values, profile), cancellationToken);
            if (started.Accepted)
            {
                delivery.RegisterSession(started.Session!.Id, userId, message.Chat.Id,
                    environment?.HasSecrets == true);
                SyncActiveSession(userId);
                await SendReplyAsync(message.Chat.Id,
                    $"Sessão {started.Session.Id} iniciada com {agent} ({context.Label}), perfil {profile.ToString().ToLowerInvariant()}. Envie uma mensagem para iniciar o turno.",
                    cancellationToken);
            }
            else await SendReplyAsync(message.Chat.Id,
                environment?.HasSecrets == true ? "Falha ao iniciar sessão." :
                    started.Error ?? "Falha ao iniciar sessão.", cancellationToken);
            return;
        }

        if (parts[0].Equals("select", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
        {
            var selected = sessions.Select(userId, parts[1].Equals("none", StringComparison.OrdinalIgnoreCase)
                ? null : parts[1]);
            SyncActiveSession(userId);
            await SendReplyAsync(message.Chat.Id, selected.Accepted
                ? selected.Session is null ? "Nenhuma sessão ativa." : $"Sessão ativa: {selected.Session.Id}."
                : selected.Error!, cancellationToken);
            return;
        }

        if (parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase) && parts.Length <= 2)
        {
            var stopped = await sessions.InterruptAsync(userId, parts.ElementAtOrDefault(1), cancellationToken);
            await SendReplyAsync(message.Chat.Id, stopped.Accepted
                ? $"Interrupção solicitada para {stopped.Session!.Id}" + (stopped.DiscardedMessages == 0 ? "."
                    : $"; {stopped.DiscardedMessages} mensagem(ns) removida(s) da fila.")
                : stopped.Error!, cancellationToken);
            return;
        }

        if (parts[0].Equals("close", StringComparison.OrdinalIgnoreCase) && parts.Length <= 2)
        {
            var closed = await sessions.CloseAsync(userId, parts.ElementAtOrDefault(1), cancellationToken);
            SyncActiveSession(userId);
            await SendReplyAsync(message.Chat.Id, closed.Accepted
                ? $"Sessão {closed.Session!.Id} encerrada." : closed.Error!, cancellationToken);
            return;
        }

        await SendReplyAsync(message.Chat.Id, usage, cancellationToken);
    }

    private string HandlePermissionsCommand(long userId, string prompt)
    {
        if (prompt.Length == 0)
        {
            var profile = selectedProfiles.GetValueOrDefault(userId, AgentPermissionProfile.Manual);
            return $"Perfil para novas sessões: {profile.ToString().ToLowerInvariant()}. Opções: manual (recomendado), auto, plan. Use /permissions <perfil>.";
        }
        if (!TryParseProfile(prompt, out var selected))
            return "Perfil indisponível. Use /permissions manual|auto|plan. Acesso full não é oferecido.";
        selectedProfiles[userId] = selected;
        return $"Perfil para novas sessões: {selected.ToString().ToLowerInvariant()}. Sessões existentes mantêm o perfil original.";
    }

    private static bool TryParseProfile(string text, out AgentPermissionProfile profile)
    {
        profile = AgentPermissionProfile.Manual;
        if (text.Equals("manual", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            profile = AgentPermissionProfile.Auto;
            return true;
        }
        if (text.Equals("plan", StringComparison.OrdinalIgnoreCase))
        {
            profile = AgentPermissionProfile.Plan;
            return true;
        }
        return false;
    }

    private async Task HandleRequestCommandAsync(TelegramMessage message, string command, string prompt,
        CancellationToken cancellationToken)
    {
        var parts = prompt.Split([' ', '\t', '\r', '\n'], 4, StringSplitOptions.RemoveEmptyEntries);
        var usage = $"Uso: {command} <sessionId> <turnId> <requestId>" +
            (command.Equals("/input", StringComparison.OrdinalIgnoreCase) ? " <resposta1> [ | <resposta2> ...]" :
             command.Equals("/deny", StringComparison.OrdinalIgnoreCase) ? " [motivo]" : "");
        if (sessions is null || parts.Length < 3 ||
            (command.Equals("/input", StringComparison.OrdinalIgnoreCase) && parts.Length < 4) ||
            ((command.Equals("/approve", StringComparison.OrdinalIgnoreCase) ||
              command.Equals("/approve-session", StringComparison.OrdinalIgnoreCase)) && parts.Length != 3))
        {
            await SendReplyAsync(message.Chat.Id, sessions is null ? "Sessões indisponíveis." : usage,
                cancellationToken);
            return;
        }

        var request = sessions.GetPendingRequest(message.From!.Id, parts[2]);
        if (request is null || !request.SessionId.Equals(parts[0], StringComparison.OrdinalIgnoreCase) ||
            !request.TurnId.Equals(parts[1], StringComparison.OrdinalIgnoreCase))
        {
            await SendReplyAsync(message.Chat.Id, "Solicitação não encontrada neste turno da sessão.",
                cancellationToken);
            return;
        }

        AgentUserResponse response;
        if (command.Equals("/input", StringComparison.OrdinalIgnoreCase))
        {
            if (request.IsApproval)
            {
                await SendReplyAsync(message.Chat.Id, "Esta solicitação espera aprovação ou negação.", cancellationToken);
                return;
            }
            string[] answers = request.Questions.Count == 1 ? [parts[3]] :
                parts[3].Split('|', StringSplitOptions.TrimEntries);
            if (answers.Length != request.Questions.Count || answers.Any(string.IsNullOrWhiteSpace))
            {
                await SendReplyAsync(message.Chat.Id,
                    $"Informe {request.Questions.Count} resposta(s) na ordem das perguntas, separadas por |.",
                    cancellationToken);
                return;
            }
            response = new AgentInputResponse(request.Questions.Select((question, index) =>
                (question.Id, Answer: answers[index])).ToDictionary(item => item.Id, item => item.Answer));
        }
        else
        {
            if (!request.IsApproval)
            {
                await SendReplyAsync(message.Chat.Id, "Esta solicitação espera uma resposta em texto.", cancellationToken);
                return;
            }
            if (command.Equals("/approve-session", StringComparison.OrdinalIgnoreCase) &&
                !request.CanApproveForSession)
            {
                await SendReplyAsync(message.Chat.Id,
                    "Aprovação para a sessão indisponível nesta solicitação.", cancellationToken);
                return;
            }
            var decision = command.Equals("/deny", StringComparison.OrdinalIgnoreCase)
                ? AgentApprovalDecision.Deny
                : command.Equals("/approve-session", StringComparison.OrdinalIgnoreCase)
                    ? AgentApprovalDecision.ApproveForSession : AgentApprovalDecision.ApproveOnce;
            response = new AgentApprovalResponse(decision, decision == AgentApprovalDecision.Deny
                ? parts.ElementAtOrDefault(3) : null);
        }

        var result = await sessions.RespondAsync(message.From.Id, parts[0], parts[1], parts[2],
            response, cancellationToken);
        await SendReplyAsync(message.Chat.Id, result.Accepted
            ? $"Resposta entregue à solicitação {parts[2]} da sessão {parts[0]}."
            : result.Error!, cancellationToken);
    }

    // Session-first conversation (AD-23): a plain message goes to the active session or, without one, opens a session
    // with the default agent in the current context and becomes its first turn. The reply is the agent's output;
    // session, turn and delivery ids stay in /status and in the explicit commands.
    private async Task ConverseAsync(TelegramMessage message, string text, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;
        var chatId = message.Chat.Id;
        var active = sessions!.GetActive(userId);
        if (active is { State: AgentSessionState.Failed or AgentSessionState.Closing or AgentSessionState.Closed })
        {
            // AD-20: an ended session stays selected so the message is refused instead of going somewhere else.
            await SendReplyAsync(chatId,
                $"A sessão {active.Id} foi encerrada. Envie /session start para começar outra conversa.",
                cancellationToken);
            return;
        }

        var sessionId = active?.Id;
        if (sessionId is null)
        {
            string? alias = null;
            var firstSpace = text.IndexOfAny([' ', '\t', '\r', '\n']);
            if (text.StartsWith('@'))
            {
                alias = firstSpace < 0 ? text : text[..firstSpace];
                text = firstSpace < 0 ? string.Empty : text[(firstSpace + 1)..].Trim();
                if (text.Length == 0)
                {
                    await SendReplyAsync(chatId, "Uso: @alias <mensagem>", cancellationToken);
                    return;
                }
            }

            var resolved = await ResolveSessionContextAsync(userId, alias, chatId, cancellationToken);
            if (resolved is null) return;
            var (context, environment) = resolved.Value;
            var agent = (settings?.Current ?? AssistantSettings.Default).DefaultAgent;
            await SendTypingAsync(chatId, cancellationToken);
            var started = await sessions.StartAsync(new SessionStartRequest(userId, agent, context,
                environment?.Values, selectedProfiles.GetValueOrDefault(userId, AgentPermissionProfile.Manual)),
                cancellationToken);
            if (!started.Accepted)
            {
                await SendReplyAsync(chatId, $"Não foi possível iniciar a conversa com {agent}. Detalhes em /status.",
                    cancellationToken);
                return;
            }
            delivery.RegisterSession(started.Session!.Id, userId, chatId, environment?.HasSecrets == true);
            SyncActiveSession(userId);
            sessionId = started.Session.Id;
        }

        var result = await sessions.SubmitAsync(userId, sessionId, text, MessageDelivery.Queue, cancellationToken);
        var reply = result.Outcome switch
        {
            SubmitOutcome.TurnStarted => null,
            SubmitOutcome.Queued => "Recebido; envio ao agente quando a resposta atual terminar.",
            _ => result.Error ?? "A sessão recusou a mensagem."
        };
        if (reply is not null) await SendReplyAsync(chatId, reply, cancellationToken);
    }

    private async Task<(JobExecutionContext Context, ResolvedRepositoryEnvironment? Environment)?>
        ResolveSessionContextAsync(long userId, string? alias, long chatId, CancellationToken cancellationToken)
    {
        var fromActive = alias is null;
        alias ??= settings?.GetActiveRepository(userId);
        if (alias is null)
        {
            var path = generalWorkspace.Path;
            if (repositories?.List().Any(repository => IsWithin(path, repository.Path) ||
                IsWithin(repository.Path, path)) == true)
            {
                await SendReplyAsync(chatId,
                    "O workspace geral coincide com um repositório cadastrado; configure DANTE_GENERAL_WORKSPACE fora dos projetos.",
                    cancellationToken);
                return null;
            }
            return (JobExecutionContext.General(path), null);
        }

        RepositoryDefinition? repository;
        try { repository = repositories?.Get(alias); }
        catch (ArgumentException)
        {
            await SendReplyAsync(chatId, "Alias inválido.", cancellationToken);
            return null;
        }
        if (repository is null)
        {
            // A stale active repository requires a new selection instead of silently falling back to General.
            await SendReplyAsync(chatId, fromActive
                ? $"O repositório ativo {alias} não está mais cadastrado. Use /use @alias ou /use general."
                : $"Repositório {alias} não cadastrado. Use /use @alias ou /use general.", cancellationToken);
            return null;
        }
        try
        {
            return (JobExecutionContext.Repository(repository.Alias, repository.Path),
                repositories!.ResolveEnvironment(repository.Alias));
        }
        catch (InvalidOperationException exception)
        {
            await SendReplyAsync(chatId, exception.Message, cancellationToken);
            return null;
        }
    }

    private async Task SubmitToSessionAsync(TelegramMessage message, string text, MessageDelivery mode,
        CancellationToken cancellationToken)
    {
        if (sessions is null || text.Length == 0)
        {
            await SendReplyAsync(message.Chat.Id,
                mode == MessageDelivery.Steer ? "Uso: /steer <orientação>" : "Sessão indisponível.",
                cancellationToken);
            return;
        }
        var result = await sessions.SubmitAsync(message.From!.Id, null, text, mode, cancellationToken);
        var response = result.Outcome switch
        {
            SubmitOutcome.TurnStarted => $"Turno {result.TurnId} iniciado na sessão {result.SessionId}.",
            SubmitOutcome.Queued => $"Mensagem enfileirada na sessão {result.SessionId}.",
            SubmitOutcome.Steered => $"Orientação enviada ao turno {result.TurnId}.",
            SubmitOutcome.SteerByInterrupt => $"Turno {result.TurnId} interrompido; orientação será enviada em seguida.",
            _ => result.Error ?? "A sessão recusou a mensagem."
        };
        await SendReplyAsync(message.Chat.Id, response, cancellationToken);
    }

    private string HandleAgentCommand(long userId, string prompt)
    {
        const string usage = "Uso: /agent | /agent set claude|codex";
        if (settings is null) return "Configurações do assistente indisponíveis.";
        var parts = prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return $"Agente padrão: {settings.Current.DefaultAgent}";
        if (parts.Length != 2 || !parts[0].Equals("set", StringComparison.OrdinalIgnoreCase)) return usage;
        if (!AssistantSettingsStore.TryParseAgent(parts[1], out var agent))
            return $"Agente desconhecido: {parts[1]}. Use claude ou codex.";
        try
        {
            var changed = settings.SetDefaultAgent(agent).DefaultAgent;
            return $"Agente padrão alterado para {changed}." +
                KeptSessionNotice(userId, session => session.Agent != changed, $"conversar com {changed}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Não foi possível salvar as configurações do assistente.";
        }
    }

    private string HandleUseCommand(long userId, string prompt)
    {
        const string usage = "Uso: /use | /use @alias | /use general";
        if (settings is null) return "Configurações do assistente indisponíveis.";
        var parts = prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        try
        {
            if (parts.Length == 0)
            {
                var active = settings.GetActiveRepository(userId);
                return active is null ? "Contexto ativo: General" :
                    repositories?.Get(active) is null
                        ? $"Contexto ativo: {active} (não cadastrado; use /use @alias ou /use general)"
                        : $"Contexto ativo: {active}";
            }
            if (parts.Length != 1) return usage;
            if (parts[0].Equals("general", StringComparison.OrdinalIgnoreCase))
            {
                settings.SetActiveRepository(userId, null);
                return "Contexto ativo: General" + KeptSessionNotice(userId,
                    session => session.Context.Mode != JobExecutionMode.General, "conversar em General");
            }
            if (!parts[0].StartsWith('@')) return usage;

            RepositoryDefinition? repository;
            try { repository = repositories?.Get(parts[0]); }
            catch (ArgumentException) { return "Alias inválido."; }
            if (repository is null) return $"Repositório {parts[0]} não cadastrado.";
            settings.SetActiveRepository(userId, repository.Alias);
            return $"Contexto ativo: {repository.Alias}" + KeptSessionNotice(userId,
                session => session.Context.RepositoryAlias != repository.Alias, $"conversar em {repository.Alias}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Não foi possível salvar as configurações do assistente.";
        }
    }

    // The delivery identifies output of every session other than the one selected here (AD-23).
    private void SyncActiveSession(long userId) => delivery.SetActiveSession(userId, sessions?.GetActive(userId)?.Id);

    // AD-20: preferences apply to new sessions only; say that the active one is kept instead of switching silently.
    private string KeptSessionNotice(long userId, Func<AgentSessionSnapshot, bool> differs, string purpose) =>
        sessions?.GetActive(userId) is { State: not (AgentSessionState.Failed or AgentSessionState.Closing or
            AgentSessionState.Closed) } active && differs(active)
            ? $"\nA sessão ativa {active.Id} ({active.Agent}, {active.Context.Label}) continua; envie /session start para {purpose}."
            : string.Empty;

    private void ClearActiveRepository(string alias)
    {
        try { settings?.ClearActiveRepository(alias); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The repository is already removed; a stale active context is still rejected at execution time.
            logger.LogWarning("Falha ao limpar o repositório ativo removido ({ErrorType}).", exception.GetType().Name);
        }
    }

    private static string FormatRepository(RepositoryDefinition repository) =>
        $"{repository.Alias}: {repository.Path}" +
        (repository.GitHub is null ? string.Empty : $" ({repository.GitHub})");

    private static string[]? SplitCommandArguments(string input)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var character in input)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (!started) continue;
                arguments.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(character);
                started = true;
            }
        }
        if (quoted) return null;
        if (started) arguments.Add(current.ToString());
        return arguments.ToArray();
    }

    private async Task HandleRepositoryEnvironmentCommandAsync(long chatId, string[] parts,
        CancellationToken cancellationToken)
    {
        const string usage = "Uso: /repo env list @alias | set @alias KEY VALUE | bind @alias KEY HOST_ENV | remove @alias KEY";
        string response;
        try
        {
            if (parts.Length == 3 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var repository = repositories!.Get(parts[2]);
                response = repository is null ? "Repositório não cadastrado." :
                    repository.Environment is not { Count: > 0 } ? "Nenhuma variável configurada." :
                    string.Join('\n', repository.Environment.Select(entry => entry.HostVariable is null
                        ? $"{entry.Key} (literal)" : $"{entry.Key} (host: {entry.HostVariable})"));
            }
            else if (parts.Length >= 5 && parts[1].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                repositories!.SetLiteral(parts[2], parts[3], string.Join(' ', parts.Skip(4)));
                response = $"Variável {parts[3]} configurada como literal. Não use env set para segredos.";
            }
            else if (parts.Length == 5 && parts[1].Equals("bind", StringComparison.OrdinalIgnoreCase))
            {
                repositories!.Bind(parts[2], parts[3], parts[4]);
                response = $"Variável {parts[3]} vinculada à variável do host {parts[4]}.";
            }
            else if (parts.Length == 4 && parts[1].Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                response = repositories!.RemoveEnvironment(parts[2], parts[3])
                    ? $"Variável {parts[3]} removida." : "Variável não configurada.";
            }
            else response = usage;
        }
        catch (ArgumentException exception) { response = exception.Message; }
        catch (IOException) { response = "Não foi possível salvar o catálogo de repositórios."; }
        await SendReplyAsync(chatId, response, cancellationToken);
    }

    private async Task RunJobAsync(string id, string agent, bool isCodex, string prompt,
        JobExecutionContext context, ResolvedRepositoryEnvironment? repositoryEnvironment, long userId, long chatId,
        CancellationToken jobToken, CancellationToken stoppingToken)
    {
        AgentProcessResult? result = null;
        var status = AgentProcessStatus.Cancelled;
        string? errorMessage = null;
        try
        {
            if (jobs.TryStart(id))
            {
                result = isCodex
                    ? await codexRunner.RunAsync(prompt, context.WorkingDirectory, jobToken,
                        context.Mode == JobExecutionMode.General,
                        repositoryEnvironment?.Values)
                    : await claudeRunner.RunAsync(prompt, context.WorkingDirectory, jobToken,
                        context.Mode == JobExecutionMode.General,
                        repositoryEnvironment?.Values);
                status = result.Status;
                errorMessage = result.ErrorMessage;
            }
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            status = AgentProcessStatus.Cancelled;
        }
        catch (Exception exception)
        {
            status = AgentProcessStatus.Failed;
            errorMessage = "Erro interno de execução.";
            logger.LogError("Falha inesperada no runner {Agent} ({ErrorType}).", agent,
                exception.GetType().Name);
        }

        var completed = jobs.Complete(id, status, result?.ExitCode, errorMessage);
        var label = completed.Context.Label;
        var response = completed.Status switch
        {
            _ when repositoryEnvironment?.HasSecrets == true =>
                $"{agent} {completed.Status}. Job {id} ({label}). Saída omitida para proteger segredos do ambiente.",
            JobStatus.Succeeded => $"{agent} concluído. Job {id} ({label}).\n\n{OutputOrFallback(result!.StandardOutput)}",
            JobStatus.Cancelled => $"{agent} cancelado. Job {id} ({label}).",
            _ => $"{agent} falhou. Job {id} ({label}).\n\n{(result is null ? errorMessage : FailureDetails(result))}"
        };

        try
        {
            await delivery.DeliverJobAsync(id, userId, chatId, response, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The worker is shutting down; the job state is already final.
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao informar o resultado do job {JobId} ({ErrorType}).", id,
                exception.GetType().Name);
        }
    }

    private static string FormatJob(JobSnapshot job) =>
        $"{job.Id} {job.Agent} {job.Context.Label}: {job.Status}" +
        (job.CancellationRequested && job.Status is JobStatus.Queued or JobStatus.Running
            ? " (cancelamento solicitado)" : string.Empty) +
        $" | criado {job.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";

    private static string FormatSession(AgentSessionSnapshot session) =>
        $"{session.Id} {session.Agent} {session.Context.Label}: {session.State}" +
        (session.IsActive ? " (ativa)" : string.Empty) +
        (session.ActiveTurnId is null ? string.Empty : $" | turno {session.ActiveTurnId}") +
        (session.QueuedCount == 0 ? string.Empty : $" | {session.QueuedCount} na fila") +
        (session.PendingRequestIds.Count == 0 ? string.Empty :
            $" | aguardando {string.Join(", ", session.PendingRequestIds)}") +
        (session.LastTurnOutcome is null ? string.Empty :
            $" | último turno {session.LastTurnOutcome switch
            {
                AgentTurnOutcome.Completed => "concluído",
                AgentTurnOutcome.Interrupted => "interrompido",
                _ => "falhou"
            }}") +
        $" | perfil {session.Profile} | criada {session.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative));
    }

    private async Task SendTypingAsync(long chatId, CancellationToken cancellationToken)
    {
        try { await botApi.SendChatActionAsync(chatId, "typing", cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            // Presence is cosmetic; the conversation continues without it.
            logger.LogDebug("Falha ao indicar digitação ({ErrorType}).", exception.GetType().Name);
        }
    }

    private async Task SendLongMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        text = RedactHostAuthSecrets(text);
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(MaxMessageLength, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
            {
                length--;
            }

            await SendReplyAsync(chatId, text.Substring(start, length), cancellationToken);
            start += length;
        }
    }

    private async Task SendReplyAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await botApi.SendMessageAsync(chatId, RedactHostAuthSecrets(text), cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < 3 &&
                (exception is HttpRequestException http &&
                    (http.StatusCode is null or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500) ||
                 exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                if (exception is TelegramRateLimitException { RetryAfter: { } retryAfter } &&
                    retryAfter > backoff) backoff = retryAfter;
                await Task.Delay(backoff, cancellationToken);
            }
        }
    }

    private static string OutputOrFallback(string output) =>
        string.IsNullOrWhiteSpace(output) ? "(sem saída)" : output;

    private static string RedactHostAuthSecrets(string message)
    {
        foreach (var name in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                message = message.Replace(value, "[segredo omitido]", StringComparison.Ordinal);
        }
        return message;
    }

    private static string FailureDetails(AgentProcessResult result)
    {
        var details = new[] { result.ErrorMessage, result.StandardError, result.StandardOutput }
            .Where(detail => !string.IsNullOrWhiteSpace(detail));
        return OutputOrFallback(string.Join("\n\n", details));
    }
}
