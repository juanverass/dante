using Dante.Worker.Agents;
using Dante.Worker.Artifacts;
using Dante.Worker.Attachments;
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
    TelegramDeliveryService? delivery = null,
    IAgentModelCatalog? models = null,
    AttachmentStore? attachments = null,
    PendingAttachments? pendingAttachments = null,
    ArtifactStore? artifacts = null) : BackgroundService
{
    private const int MaxMessageLength = 4000;
    private const string EffortOption = "effort=";
    private const string ModelOption = "model=";
    private readonly object runningGate = new();
    private readonly HashSet<Task> runningJobs = [];
    // Default modes when no settings store is composed; otherwise they persist in settings.json (#76).
    private readonly Dictionary<long, AgentPermissionProfile> selectedProfiles = [];
    private readonly AgentContextResolver resolver =
        new(generalWorkspace ?? new GeneralWorkspace(), repositories, settings);
    private readonly TelegramDeliveryService delivery = delivery ??
        new TelegramDeliveryService(botApi, NullLogger<TelegramDeliveryService>.Instance);
    // Media intake (#94) exists only when an attachment store is composed.
    private readonly PendingAttachments? pending = attachments is null ? null :
        pendingAttachments ?? new PendingAttachments(attachments);
    private TelegramMediaReceiver? mediaReceiver;
    // Updates are handled one at a time; an album that completes later joins the same line (#95).
    private readonly SemaphoreSlim updateGate = new(1, 1);

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

        SweepAttachments();
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
                    if (update.CallbackQuery is { } callback)
                    {
                        await HandleCallbackAsync(callback, stoppingToken);
                    }
                    // Authorization comes before anything else, including any download of a sent file.
                    else if (update.Message is { } message && (message.Text is not null || message.HasMedia)
                        && authorizer.IsAuthorized(message.From))
                    {
                        await ExclusiveAsync(() => HandleMessageAsync(message, stoppingToken), stoppingToken);
                    }
                }
                await NotifyExpiredAttachmentsAsync(stoppingToken);
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

    private async Task HandleCallbackAsync(TelegramCallbackQuery callback, CancellationToken cancellationToken)
    {
        var reply = "Solicitação indisponível ou já respondida.";
        try
        {
            if (authorizer.IsAuthorized(callback.From) && sessions is not null &&
                callback.Message is { } message &&
                TelegramApprovalCallback.TryParse(callback.Data, out var action))
            {
                var pending = sessions.GetPendingRequest(callback.From.Id, action.RequestId);
                if (pending is { IsApproval: true } &&
                    delivery.OwnsApprovalMessage(action.RequestId, callback.From.Id, message.Chat.Id, message.MessageId))
                {
                    var result = await sessions.RespondAsync(callback.From.Id, action.SessionId, action.TurnId,
                        action.RequestId, new AgentApprovalResponse(action.Decision), cancellationToken);
                    if (result.Accepted) reply = TelegramApprovalCallback.DecisionText(action.Decision);
                }
            }
        }
        finally
        {
            try { await botApi.AnswerCallbackAsync(callback.Id, reply, cancellationToken); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Falha ao confirmar callback ({ErrorType}).", exception.GetType().Name);
            }
        }
    }

    private async Task HandleMessageAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        // An album still open completes before anything its sender sent after it, so requests keep their order (#95).
        if (mediaReceiver is not null) await mediaReceiver.CompleteAlbumsAsync(message.From!.Id, message.MediaGroupId);
        if (message.Text is null)
        {
            if (pending is null)
            {
                await SendReplyAsync(message.Chat.Id, "Recebimento de mídias indisponível.", cancellationToken);
                return;
            }
            mediaReceiver ??= new TelegramMediaReceiver(botApi, attachments!, pending, logger, AttachmentContext,
                ExclusiveAsync);
            await mediaReceiver.ReceiveAsync(message, (reply, token) => SendReplyAsync(message.Chat.Id, reply, token),
                (caption, token) => HandleCaptionAsync(message, caption, token), cancellationToken);
            return;
        }

        await HandleTextAsync(message, message.Text.Trim(), cancellationToken);
    }

    private async Task ExclusiveAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try { await action(); }
        finally { updateGate.Release(); }
    }

    // A caption is the request of its images (AD-29): a conversation message or a /claude or /codex one-shot. Any other
    // command is refused and the images stay pending for the next text.
    private async Task HandleCaptionAsync(TelegramMessage message, string caption, CancellationToken cancellationToken)
    {
        var separator = caption.IndexOfAny([' ', '\t', '\r', '\n']);
        var command = separator < 0 ? caption : caption[..separator];
        if (caption.StartsWith('/') && !command.Equals("/claude", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("/codex", StringComparison.OrdinalIgnoreCase))
        {
            await SendReplyAsync(message.Chat.Id, $"Comando desconhecido na legenda: {command}. As imagens continuam " +
                "pendentes; envie o pedido em texto.", cancellationToken);
            return;
        }
        await HandleTextAsync(message, caption, cancellationToken);
    }

    private async Task HandleTextAsync(TelegramMessage message, string text, CancellationToken cancellationToken)
    {
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
            await DiscardStalePendingAsync(message, cancellationToken);
            return;
        }

        if (string.Equals(command, "/use", StringComparison.OrdinalIgnoreCase))
        {
            await SendReplyAsync(message.Chat.Id, HandleUseCommand(message.From!.Id, prompt),
                cancellationToken);
            await DiscardStalePendingAsync(message, cancellationToken);
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
            if (pending?.Get(message.From!.Id) is { } batch)
                response += $"\n\nAnexos pendentes: {batch.Items.Count} imagem(ns), {batch.Bytes / 1024} KB, " +
                    $"expiram às {batch.ExpiresAtUtc:HH:mm:ss} UTC.";
            var deliveries = visible.Select(job => delivery.Get(job.Id, message.From!.Id))
                .Concat(ownSessions.Select(session => delivery.Get(session.Id, message.From!.Id)))
                .Concat(delivery.ListArtifacts(message.From!.Id))
                .Where(snapshot => snapshot is not null).ToArray();
            if (deliveries.Length > 0)
                response += "\n\nEntregas Telegram:\n" + string.Join('\n', deliveries.Select(snapshot =>
                    $"{snapshot!.Id}: {snapshot.State} ({snapshot.DeliveredChunks}/{snapshot.TotalChunks} partes)" +
                    (snapshot.Problem is null ? string.Empty : $" | {snapshot.Problem}")));
            await SendLongMessageAsync(message.Chat.Id, response, cancellationToken);
            return;
        }

        if (string.Equals(command, "/session", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSessionCommandAsync(message, prompt, cancellationToken);
            await DiscardStalePendingAsync(message, cancellationToken);
            return;
        }

        if (string.Equals(command, "/mode", StringComparison.OrdinalIgnoreCase))
        {
            await SendReplyAsync(message.Chat.Id, HandleModeCommand(message.From!.Id, prompt), cancellationToken);
            return;
        }

        if (string.Equals(command, "/effort", StringComparison.OrdinalIgnoreCase))
        {
            await SendLongMessageAsync(message.Chat.Id, await HandleEffortCommandAsync(message.From!.Id, prompt,
                cancellationToken), cancellationToken);
            return;
        }

        if (string.Equals(command, "/model", StringComparison.OrdinalIgnoreCase))
        {
            await SendLongMessageAsync(message.Chat.Id, await HandleModelCommandAsync(message.From!.Id, prompt,
                cancellationToken), cancellationToken);
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
                await SendReplyAsync(message.Chat.Id, "Uso: /resend <jobId|sessionId[/turnId]|arquivo>", cancellationToken);
                return;
            }
            var recovered = await delivery.RetryAsync(prompt, message.From!.Id, message.Chat.Id, cancellationToken);
            await SendReplyAsync(message.Chat.Id, recovered is null
                ? "Saída recente não encontrada para reenvio."
                : $"Entrega {recovered.Id}: {recovered.State} ({recovered.DeliveredChunks}/{recovered.TotalChunks} partes)" +
                  (recovered.Problem is null ? "." : $": {recovered.Problem}."),
                cancellationToken);
            return;
        }

        if (string.Equals(command, "/send", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSendCommandAsync(message, prompt, cancellationToken);
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

        AgentKind? explicitAgent = string.Equals(command, "/codex", StringComparison.OrdinalIgnoreCase)
            ? AgentKind.Codex
            : string.Equals(command, "/claude", StringComparison.OrdinalIgnoreCase) ? AgentKind.Claude : null;
        if (explicitAgent is null)
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
            // Without a session registry, plain messages fall back to one-shot jobs of the default agent.
            prompt = text;
        }

        var resolution = resolver.Resolve(message.From!.Id, explicitAgent, prompt);
        var agent = resolution.Agent.ToString();
        if (!resolution.Succeeded)
        {
            await SendReplyAsync(message.Chat.Id, resolution.Failure == ContextResolutionFailure.EmptyPrompt
                ? $"Uso: /{agent.ToLowerInvariant()} [@alias] <prompt>" : resolution.Error!, cancellationToken);
            return;
        }

        // One-shot jobs use the user's default model of the agent they name; an unavailable one stops here (#77).
        var modelSelection = await ResolveModelAsync(message.From!.Id, resolution.Agent, null, message.Chat.Id,
            cancellationToken);
        if (modelSelection is null) return;
        var context = resolution.Context!;
        var (job, jobToken) = jobs.Create(agent, context, cancellationToken, modelSelection);
        // The pending images go with this one-shot and live in the job's directory until it ends (AD-29).
        var images = Adopt(message.From.Id, AttachmentContext(message.From.Id), job.Id, newScope: true);
        if (images is null)
        {
            jobs.Complete(job.Id, AgentProcessStatus.Failed, errorMessage: ImagesUnavailable);
            await SendReplyAsync(message.Chat.Id, ImagesUnavailable, cancellationToken);
            return;
        }
        try
        {
            await SendReplyAsync(message.Chat.Id,
                $"{agent} iniciado. Job ID: {job.Id} ({context.Label}){ModelSuffix(modelSelection)}{ImageSuffix(images)}." +
                OverrideNotice(message.From.Id, resolution), cancellationToken);
        }
        catch
        {
            jobs.Complete(job.Id, AgentProcessStatus.Failed, errorMessage: "Falha ao confirmar o início.");
            ReleaseJobImages(message.From.Id, job.Id, images);
            throw;
        }

        var task = RunJobAsync(job.Id, agent, resolution.Agent == AgentKind.Codex, resolution.Prompt, context,
            resolution.Environment, modelSelection, images, message.From!.Id, message.Chat.Id, jobToken,
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
        const string usage = "Uso: /session start [claude|codex] [@alias] [manual|auto|plan] [model=<modelo>] [effort=<nível>] | list | select <id|none> | stop [id] | close [id]";
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
            AgentKind? requestedAgent = null;
            var profile = DefaultMode(userId);
            string? alias = null;
            string? model = null;
            string? effort = null;
            var profileSpecified = false;
            for (var index = 1; index < parts.Length; index++)
            {
                if (parts[index].Equals("claude", StringComparison.OrdinalIgnoreCase)) requestedAgent = AgentKind.Claude;
                else if (parts[index].Equals("codex", StringComparison.OrdinalIgnoreCase)) requestedAgent = AgentKind.Codex;
                else if (parts[index].StartsWith('@') && alias is null) alias = parts[index];
                else if (parts[index].StartsWith(ModelOption, StringComparison.OrdinalIgnoreCase) && model is null &&
                         parts[index].Length > ModelOption.Length) model = parts[index][ModelOption.Length..];
                else if (parts[index].StartsWith(EffortOption, StringComparison.OrdinalIgnoreCase) && effort is null &&
                         parts[index].Length > EffortOption.Length) effort = parts[index][EffortOption.Length..];
                else if (AgentSessionModes.TryParse(parts[index], out var requested) && !profileSpecified)
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
            var resolved = resolver.ResolveContext(userId, requestedAgent, alias);
            if (!resolved.Succeeded)
            {
                await SendReplyAsync(message.Chat.Id, resolved.Error!, cancellationToken);
                return;
            }
            var (agent, context, environment) = (resolved.Agent, resolved.Context!, resolved.Environment);
            // A model given here is for this session only and, like the default, is checked before any process starts.
            var modelSelection = await ResolveModelAsync(userId, agent, model, message.Chat.Id, cancellationToken, effort);
            if (modelSelection is null) return;
            var started = await sessions.StartAsync(new SessionStartRequest(userId, agent, context,
                environment?.Values, profile, modelSelection), cancellationToken);
            if (started.Accepted)
            {
                delivery.RegisterSession(started.Session!.Id, userId, message.Chat.Id,
                    environment?.HasSecrets == true);
                SyncActiveSession(userId);
                await SendReplyAsync(message.Chat.Id,
                    $"Sessão {started.Session.Id} iniciada com {agent} ({context.Label}), modo {AgentSessionModes.Label(profile)}{ModelSuffix(modelSelection)}. Envie uma mensagem para iniciar o turno.",
                    cancellationToken);
            }
            // Without a session the request was refused before any process started (e.g. unsupported mode).
            else await SendReplyAsync(message.Chat.Id,
                environment?.HasSecrets == true && started.Session is not null ? "Falha ao iniciar sessão." :
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

    // Modes are the user-facing permission profiles (#76): the default applies to new sessions only, and a session
    // keeps the mode it started with until it is closed (AD-20).
    private string HandleModeCommand(long userId, string prompt)
    {
        const string usage = "Uso: /mode | /mode manual|auto|plan";
        var parts = prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0].Equals("set", StringComparison.OrdinalIgnoreCase)) parts = [parts[1]];
        if (parts.Length == 0)
        {
            var lines = new List<string>
            {
                $"Modo padrão para novas sessões: {AgentSessionModes.Label(DefaultMode(userId))}.",
                "Modos:"
            };
            lines.AddRange(AgentSessionModes.All.Select(mode =>
                $"- {AgentSessionModes.Label(mode)}: {AgentSessionModes.Description(mode)}"));
            lines.Add("Suporte: " + string.Join(" | ", new[] { AgentKind.Claude, AgentKind.Codex }.Select(agent =>
                $"{agent} {string.Join(", ", AgentDriverCapabilities.For(agent).Modes.Select(AgentSessionModes.Name))}")));
            if (sessions?.GetActive(userId) is { State: not (AgentSessionState.Failed or AgentSessionState.Closing or
                    AgentSessionState.Closed) } active)
                lines.Add($"Sessão ativa {active.Id}: modo {AgentSessionModes.Name(active.Profile)}, fixo até ela ser encerrada.");
            lines.Add("Use /mode <modo> para novas sessões ou /session start [claude|codex] [@alias] <modo>. " +
                "Acesso irrestrito (full) não é oferecido.");
            return string.Join('\n', lines);
        }
        if (parts.Length != 1) return usage;
        if (!AgentSessionModes.TryParse(parts[0], out var selected))
            return $"Modo indisponível: {parts[0]}. Use /mode manual|auto|plan. Acesso irrestrito (full) não é oferecido.";
        if (!TrySetDefaultMode(userId, selected)) return "Não foi possível salvar as configurações do assistente.";
        return $"Modo padrão para novas sessões: {AgentSessionModes.Label(selected)}. Sessões existentes mantêm o modo original." +
            KeptSessionNotice(userId, session => session.Profile != selected,
                $"usar o modo {AgentSessionModes.Name(selected)}");
    }

    // Low-level alias of /mode kept from #67.
    private string HandlePermissionsCommand(long userId, string prompt)
    {
        if (prompt.Length == 0)
        {
            var profile = DefaultMode(userId);
            return $"Perfil para novas sessões: {AgentSessionModes.Name(profile)}. Opções: manual (recomendado), auto, plan. Use /permissions <perfil> ou /mode.";
        }
        if (!AgentSessionModes.TryParse(prompt, out var selected))
            return "Perfil indisponível. Use /permissions manual|auto|plan. Acesso full não é oferecido.";
        if (!TrySetDefaultMode(userId, selected)) return "Não foi possível salvar as configurações do assistente.";
        return $"Perfil para novas sessões: {AgentSessionModes.Name(selected)}. Sessões existentes mantêm o perfil original.";
    }

    private AgentPermissionProfile DefaultMode(long userId) =>
        settings?.GetSessionMode(userId) ?? selectedProfiles.GetValueOrDefault(userId, AgentPermissionProfile.Manual);

    private bool TrySetDefaultMode(long userId, AgentPermissionProfile mode)
    {
        if (settings is null)
        {
            selectedProfiles[userId] = mode;
            return true;
        }
        try
        {
            settings.SetSessionMode(userId, mode);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Models per agent and user (#77): the default applies to new sessions and one-shot jobs, a session keeps the model
    // it started with, and only a model the installed CLI offers is accepted. "default" returns to the CLI's own choice.
    private async Task<string> HandleModelCommandAsync(long userId, string prompt, CancellationToken cancellationToken)
    {
        const string usage = "Uso: /model | /model claude|codex [<modelo>|default]";
        if (settings is null) return "Configurações do assistente indisponíveis.";
        var parts = prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts[0].Equals("set", StringComparison.OrdinalIgnoreCase)) parts = parts[1..];
        if (parts.Length == 0)
        {
            var lines = new List<string> { "Modelo padrão para novas sessões e execuções one-shot:" };
            lines.AddRange(new[] { AgentKind.Claude, AgentKind.Codex }.Select(agent =>
                $"- {agent}: {new AgentModelSelection(settings.GetModel(userId, agent)).ModelLabel}"));
            if (sessions?.GetActive(userId) is { State: not (AgentSessionState.Failed or AgentSessionState.Closing or
                    AgentSessionState.Closed) } active)
                lines.Add($"Sessão ativa {active.Id}: {active.Agent}, modelo {active.ModelLabel}, fixo até ela ser encerrada.");
            lines.Add("Use /model claude|codex para ver os modelos oferecidos pela CLI instalada, " +
                "/model <agente> <modelo> para escolher ou /model <agente> default para voltar ao padrão da CLI.");
            return string.Join('\n', lines);
        }
        if (parts.Length > 2 || !AssistantSettingsStore.TryParseAgent(parts[0], out var selectedAgent)) return usage;
        var name = selectedAgent.ToString().ToLowerInvariant();
        var current = settings.GetModel(userId, selectedAgent);
        if (parts.Length == 1)
        {
            if (models is null) return "Catálogo de modelos indisponível.";
            IReadOnlyList<AgentModelInfo> offered;
            try { offered = await models.GetModelsAsync(selectedAgent, cancellationToken); }
            catch (AgentModelCatalogException exception)
            {
                return $"Não foi possível consultar os modelos do {selectedAgent}: {exception.Message}.";
            }
            var lines = new List<string>
            {
                $"Modelo padrão do {selectedAgent}: {new AgentModelSelection(current).ModelLabel}.",
                $"Modelos oferecidos pelo {selectedAgent} instalado:"
            };
            lines.AddRange(offered.Select(model =>
                $"- {model.Id}" +
                (model.DisplayName == model.Id && model.ResolvedId is null ? string.Empty
                    : $" ({model.DisplayName}{(model.ResolvedId is null ? string.Empty : $" → {model.ResolvedId}")})") +
                (model.IsDefault ? " — padrão da CLI" : string.Empty) +
                (current is not null && AgentModelCatalog.Find([model], current) is not null ? " — escolhido" : string.Empty)));
            lines.Add($"Use /model {name} <modelo> ou /model {name} default.");
            return string.Join('\n', lines);
        }

        AgentModelSelection selected;
        if (parts[1].Equals("default", StringComparison.OrdinalIgnoreCase)) selected = AgentModelSelection.CliDefault;
        else
        {
            if (!AgentModelSelection.IsValidName(parts[1])) return $"Nome de modelo inválido: {parts[1]}.";
            var (found, error) = await FindModelAsync(selectedAgent, parts[1], cancellationToken);
            if (found is null) return error ?? NotOffered(selectedAgent, parts[1]);
            selected = new AgentModelSelection(found);
        }
        try { settings.SetModel(userId, selectedAgent, selected.Model); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Não foi possível salvar as configurações do assistente.";
        }
        return $"Modelo padrão do {selectedAgent}: {selected.ModelLabel}. Vale para novas sessões e para /{name}; " +
            "sessões existentes mantêm o modelo original." +
            KeptSessionNotice(userId, session => session.Agent == selectedAgent &&
                    !string.Equals(session.ModelSelection?.Model, selected.Model, StringComparison.Ordinal),
                $"usar o modelo {selected.ModelLabel}");
    }

    // The model a new session or job of the agent runs with: the explicit one or the user's default, checked against
    // what the installed CLI offers now. Null means refused, with the reason already sent; no model skips the check.
    private async Task<AgentModelSelection?> ResolveModelAsync(long userId, AgentKind agent, string? requested,
        long chatId, CancellationToken cancellationToken, string? requestedEffort = null)
    {
        var name = requested ?? settings?.GetModel(userId, agent);
        if (name is null || (requested is not null && requested.Equals("default", StringComparison.OrdinalIgnoreCase)))
            return await ResolveEffortAsync(userId, agent, AgentModelSelection.CliDefault, requestedEffort, chatId,
                cancellationToken);
        if (!AgentModelSelection.IsValidName(name))
        {
            await SendReplyAsync(chatId, $"Nome de modelo inválido: {name}.", cancellationToken);
            return null;
        }
        var (found, error) = await FindModelAsync(agent, name, cancellationToken);
        if (found is not null)
            return await ResolveEffortAsync(userId, agent, new AgentModelSelection(found), requestedEffort, chatId,
                cancellationToken);
        var command = $"/model {agent.ToString().ToLowerInvariant()}";
        // A default the CLI stopped offering is never swapped for another model silently.
        await SendReplyAsync(chatId, error ?? (requested is not null ? NotOffered(agent, name) :
            $"O modelo {name}, padrão do {agent}, não é mais oferecido pelo {agent} instalado. Escolha outro com " +
            $"{command} <modelo> ou volte ao padrão da CLI com {command} default."), cancellationToken);
        return null;
    }

    private async Task<(AgentModelInfo? Model, string? Error)> EffortModelAsync(AgentKind agent, string? model,
        CancellationToken cancellationToken)
    {
        if (models is null) return (null, "Catálogo de capacidades indisponível.");
        try
        {
            var offered = await models.GetModelsAsync(agent, cancellationToken);
            var selected = model is null ? offered.FirstOrDefault(item => item.IsDefault)
                : offered.FirstOrDefault(item => AgentModelCatalog.Find([item], model) is not null);
            return selected is null
                ? (null, "Não foi possível identificar o modelo e seus níveis de esforço. Escolha um modelo com /model.")
                : (selected, null);
        }
        catch (AgentModelCatalogException exception)
        {
            return (null, $"Não foi possível consultar os níveis de esforço do {agent}: {exception.Message}.");
        }
    }

    private async Task<AgentModelSelection?> ResolveEffortAsync(long userId, AgentKind agent,
        AgentModelSelection selection, string? requested, long chatId, CancellationToken cancellationToken)
    {
        var effort = requested ?? settings?.GetEffort(userId, agent);
        if (effort is null || effort.Equals("default", StringComparison.OrdinalIgnoreCase)) return selection;
        var (model, error) = await EffortModelAsync(agent, selection.Model, cancellationToken);
        var found = model?.EffortLevels.FirstOrDefault(level => level.Equals(effort, StringComparison.OrdinalIgnoreCase));
        if (found is not null) return selection with { Effort = found };
        await SendReplyAsync(chatId, error ??
            $"Esforço {effort} incompatível com {agent}, modelo {model!.Id}. Opções: " +
            $"{string.Join(", ", model.EffortLevels)}. Use /effort {agent.ToString().ToLowerInvariant()} default para voltar ao padrão da CLI.",
            cancellationToken);
        return null;
    }

    private async Task<string> HandleEffortCommandAsync(long userId, string prompt, CancellationToken cancellationToken)
    {
        const string usage = "Uso: /effort | /effort claude|codex [<nível>|default]";
        if (settings is null) return "Configurações do assistente indisponíveis.";
        var parts = prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2 || parts.Length > 0 && !AssistantSettingsStore.TryParseAgent(parts[0], out _)) return usage;
        var agents = parts.Length == 0 ? new[] { AgentKind.Claude, AgentKind.Codex }
            : new[] { AssistantSettingsStore.TryParseAgent(parts[0], out var parsed) ? parsed : AgentKind.Claude };
        if (parts.Length == 2 && parts[1].Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            try { settings.SetEffort(userId, agents[0], null); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { return "Não foi possível salvar a preferência de esforço."; }
            return $"Esforço padrão do {agents[0]}: padrão da CLI. Sessões existentes mantêm o esforço original.";
        }
        var lines = new List<string>();
        foreach (var agent in agents)
        {
            var (model, error) = await EffortModelAsync(agent, settings.GetModel(userId, agent), cancellationToken);
            if (error is not null)
            {
                if (parts.Length == 2) return error;
                lines.Add($"{agent}: {settings.GetEffort(userId, agent) ?? "padrão da CLI"}. {error}");
                continue;
            }
            if (parts.Length == 2)
            {
                var found = model!.EffortLevels.FirstOrDefault(level => level.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                if (found is null) return $"Esforço {parts[1]} incompatível com {agent}, modelo {model.Id}. Opções: {string.Join(", ", model.EffortLevels)}.";
                try { settings.SetEffort(userId, agent, found); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { return "Não foi possível salvar a preferência de esforço."; }
                return $"Esforço padrão do {agent}: {found}. Vale para novas sessões e execuções one-shot; sessões existentes mantêm o esforço original.";
            }
            lines.Add($"{agent}: {settings.GetEffort(userId, agent) ?? "padrão da CLI"}; modelo {model!.Id}. " +
                $"Opções: {(model.EffortLevels.Count == 0 ? "nenhum nível explícito" : string.Join(", ", model.EffortLevels))}, default.");
        }
        if (sessions?.GetActive(userId) is { } active)
            lines.Add($"Sessão ativa {active.Id}: esforço {active.EffortLabel}, fixo até ela ser encerrada.");
        lines.Add("Use /effort <agente> <nível> ou default. Esforço não altera permissões.");
        return string.Join('\n', lines);
    }

    // The name as the CLI spells it; neither a name nor an error means the CLI does not offer the model.
    private async Task<(string? Found, string? Error)> FindModelAsync(AgentKind agent, string name,
        CancellationToken cancellationToken)
    {
        if (models is null) return (null, $"Não foi possível validar o modelo {name}: catálogo de modelos indisponível.");
        try { return (AgentModelCatalog.Find(await models.GetModelsAsync(agent, cancellationToken), name), null); }
        catch (AgentModelCatalogException exception)
        {
            return (null, $"Não foi possível validar o modelo {name} do {agent}: {exception.Message}. " +
                $"Tente novamente ou use /model {agent.ToString().ToLowerInvariant()} default.");
        }
    }

    private static string NotOffered(AgentKind agent, string name) =>
        $"Modelo {name} não é oferecido pelo {agent} instalado. Use /model {agent.ToString().ToLowerInvariant()} " +
        "para ver os disponíveis.";

    private static string ModelSuffix(AgentModelSelection selection) =>
        (selection.Model is null ? string.Empty : $", modelo {selection.Model}") +
        (selection.Effort is null ? string.Empty : $", esforço {selection.Effort}");

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
        // Taken from the context the conversation is in now, before a new session changes it (AD-29).
        var pendingKey = AttachmentContext(userId);
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
            var resolved = resolver.Resolve(userId, null, text);
            if (!resolved.Succeeded)
            {
                await SendReplyAsync(chatId, resolved.Failure == ContextResolutionFailure.EmptyPrompt
                    ? "Uso: @alias <mensagem>" : resolved.Error!, cancellationToken);
                return;
            }
            var (agent, context, environment) = (resolved.Agent, resolved.Context!, resolved.Environment);
            text = resolved.Prompt;
            var mode = DefaultMode(userId);
            await SendTypingAsync(chatId, cancellationToken);
            var modelSelection = await ResolveModelAsync(userId, agent, null, chatId, cancellationToken);
            if (modelSelection is null) return;
            var started = await sessions.StartAsync(new SessionStartRequest(userId, agent, context,
                environment?.Values, mode, modelSelection), cancellationToken);
            if (!started.Accepted)
            {
                // Without a session the request was refused before any process started (e.g. unsupported mode).
                await SendReplyAsync(chatId, started.Session is null
                    ? $"Não foi possível iniciar a conversa com {agent}: {started.Error}"
                    : $"Não foi possível iniciar a conversa com {agent}. Detalhes em /status.", cancellationToken);
                return;
            }
            delivery.RegisterSession(started.Session!.Id, userId, chatId, environment?.HasSecrets == true);
            SyncActiveSession(userId);
            sessionId = started.Session.Id;
        }

        var images = Adopt(userId, pendingKey, sessionId);
        if (images is null)
        {
            await SendReplyAsync(chatId, ImagesUnavailable, cancellationToken);
            return;
        }
        var result = await SessionSubmitAsync(userId, sessionId, new AgentInput(text, images), MessageDelivery.Queue,
            cancellationToken);
        var reply = result.Outcome switch
        {
            SubmitOutcome.TurnStarted => null,
            SubmitOutcome.Queued => "Recebido; envio ao agente quando a resposta atual terminar.",
            _ => result.Error ?? "A sessão recusou a mensagem."
        };
        if (reply is not null) await SendReplyAsync(chatId, reply, cancellationToken);
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
        var userId = message.From!.Id;
        IReadOnlyList<Attachment>? images = [];
        if (sessions.GetActive(userId) is { } active)
            images = Adopt(userId, AttachmentContext(userId), active.Id);
        if (images is null)
        {
            await SendReplyAsync(message.Chat.Id, ImagesUnavailable, cancellationToken);
            return;
        }
        var result = await SessionSubmitAsync(userId, null, new AgentInput(text, images), mode, cancellationToken);
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
        if (parts.Length == 0) return $"Agente padrão: {settings.Current.DefaultAgent}" + ActiveSessionNotice(userId);
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
                return (active is null ? "Contexto ativo: General" :
                    repositories?.Get(active) is null
                        ? $"Contexto ativo: {active} (não cadastrado; use /use @alias ou /use general)"
                        : $"Contexto ativo: {active}") + ActiveSessionNotice(userId);
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

    // Pending attachments belong to the context they arrived in (AD-29): the active session, or else the agent and
    // context a new conversation would use. They never move to another one.
    private string AttachmentContext(long userId)
    {
        if (sessions?.GetActive(userId) is { } active) return $"session:{active.Id}";
        var resolved = resolver.ResolveContext(userId, null, null);
        return resolved.Succeeded ? $"{resolved.Agent}:{resolved.Context!.Label}" : "unresolved";
    }

    private async Task DiscardStalePendingAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        if (pending?.DiscardIfContextChanged(message.From!.Id, AttachmentContext(message.From.Id)) is { } discarded)
            await SendReplyAsync(message.Chat.Id, $"{discarded.Items.Count} imagem(ns) pendente(s) descartada(s): " +
                "o contexto da conversa mudou.", cancellationToken);
    }

    private async Task NotifyExpiredAttachmentsAsync(CancellationToken cancellationToken)
    {
        if (pending is null) return;
        foreach (var batch in pending.RemoveExpired())
        {
            try
            {
                await SendReplyAsync(batch.ChatId, $"{batch.Items.Count} imagem(ns) pendente(s) apagada(s): nenhum " +
                    $"pedido chegou em {PendingAttachments.Expiry.TotalMinutes:0} min.", cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Falha ao avisar a expiração de anexos ({ErrorType}).", exception.GetType().Name);
            }
        }
    }

    private void SweepAttachments()
    {
        try
        {
            var removed = attachments?.SweepStale(TimeSpan.FromHours(24)) ?? 0;
            if (removed > 0) logger.LogInformation("{Count} anexo(s) antigo(s) removido(s).", removed);
            // Copies of produced files are reachable only through in-memory records, gone with the previous run.
            var copies = artifacts?.SweepAll() ?? 0;
            if (copies > 0) logger.LogInformation("{Count} cópia(s) de arquivo(s) produzido(s) removida(s).", copies);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha ao limpar anexos antigos ({ErrorType}).", exception.GetType().Name);
        }
    }

    // /send <caminho>: the user asks for a file of the active session's working directory (#97, AD-29). Only that
    // directory is accepted, with links resolved, and nothing leaves a session with bound secrets (AD-10).
    private async Task HandleSendCommandAsync(TelegramMessage message, string path, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;
        string reply;
        if (path.Length == 0) reply = "Uso: /send <caminho no diretório da sessão>";
        else if (artifacts is null || sessions is null) reply = "Envio de arquivos indisponível.";
        else if (sessions.GetActive(userId) is not { State: not (AgentSessionState.Failed or AgentSessionState.Closing or
                     AgentSessionState.Closed) } active)
            reply = "Nenhuma sessão ativa: /send envia arquivos do diretório da sessão ativa.";
        else
        {
            var (_, error) = delivery.SendFile(active.Id, userId, message.Chat.Id, path, active.Context.WorkingDirectory);
            // On success the file itself is the answer; a failed upload is reported with its id.
            if (error is null) return;
            reply = $"Arquivo não enviado: {error}.";
        }
        await SendReplyAsync(message.Chat.Id, reply, cancellationToken);
    }

    private const string ImagesUnavailable = "Não foi possível preparar as imagens pendentes; nada foi enviado ao agente.";

    // The next text that reaches an agent takes the images pending in the conversation's context and moves them to the
    // directory of the session or job that uses them (AD-29). Null: they could not be moved and were deleted. Job ids
    // restart with the process, so a job's directory is cleared first; a session's was cleared when it started.
    private IReadOnlyList<Attachment>? Adopt(long userId, string contextKey, string scope, bool newScope = false)
    {
        if (pending?.Take(userId, contextKey) is not { } batch) return [];
        var moved = new List<Attachment>();
        try
        {
            if (newScope) attachments!.DeleteScope(userId, scope);
            foreach (var item in batch.Items) moved.Add(attachments!.MoveTo(item, scope));
            return moved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha ao mover anexos para {Scope} ({ErrorType}).", scope, exception.GetType().Name);
            foreach (var item in batch.Items.Concat(moved)) DeleteQuietly(item);
            return null;
        }
    }

    // A message the session refused never reaches the agent, so its images are not kept.
    private async Task<SessionSubmitResult> SessionSubmitAsync(long userId, string? sessionId, AgentInput input,
        MessageDelivery mode, CancellationToken cancellationToken)
    {
        var result = await sessions!.SubmitAsync(userId, sessionId, input, mode, cancellationToken);
        if (result.Outcome == SubmitOutcome.Rejected)
            foreach (var image in input.Attachments) DeleteQuietly(image);
        return result;
    }

    private void ReleaseJobImages(long userId, string jobId, IReadOnlyList<Attachment> images)
    {
        if (images.Count == 0) return;
        try { attachments!.DeleteScope(userId, jobId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha ao apagar os anexos do job {JobId} ({ErrorType}).", jobId, exception.GetType().Name);
        }
    }

    private void DeleteQuietly(Attachment attachment)
    {
        try { attachments!.Delete(attachment); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha ao apagar um anexo ({ErrorType}).", exception.GetType().Name);
        }
    }

    private static string ImageSuffix(IReadOnlyList<Attachment> images) =>
        images.Count == 0 ? string.Empty : $", {images.Count} {(images.Count == 1 ? "imagem" : "imagens")}";

    // The delivery identifies output of every session other than the one selected here (AD-23).
    private void SyncActiveSession(long userId) => delivery.SetActiveSession(userId, sessions?.GetActive(userId)?.Id);

    // AD-20: preferences apply to new sessions only; say that the active one is kept instead of switching silently.
    private string KeptSessionNotice(long userId, Func<AgentSessionSnapshot, bool> differs, string purpose) =>
        sessions?.GetActive(userId) is { State: not (AgentSessionState.Failed or AgentSessionState.Closing or
            AgentSessionState.Closed) } active && differs(active)
            ? $"\nA sessão ativa {active.Id} ({active.Agent}, {active.Context.Label}) continua; envie /session start para {purpose}."
            : string.Empty;

    // Plain messages go to the live active session whatever the defaults say (AD-23), so the queries tell it (#38).
    private string ActiveSessionNotice(long userId) =>
        sessions?.GetActive(userId) is { State: not (AgentSessionState.Failed or AgentSessionState.Closing or
            AgentSessionState.Closed) } active
            ? $"\nMensagens comuns vão para a sessão ativa {active.Id} ({active.Agent}, {active.Context.Label}) até /session close."
            : string.Empty;

    // A one-shot /claude, /codex or @alias that differs from the user's defaults applies to that execution only (AD-27).
    private string OverrideNotice(long userId, AgentContextResolution resolution)
    {
        var kept = new List<string>();
        var defaultAgent = (settings?.Current ?? AssistantSettings.Default).DefaultAgent;
        if (resolution.AgentSource == AgentSource.Explicit && resolution.Agent != defaultAgent)
            kept.Add($"o agente padrão continua {defaultAgent}");
        var activeContext = settings?.GetActiveRepository(userId) ?? "General";
        if (resolution.ContextSource == ContextSource.Explicit &&
            !string.Equals(resolution.Context!.Label, activeContext, StringComparison.OrdinalIgnoreCase))
            kept.Add($"o contexto ativo continua {activeContext}");
        return kept.Count == 0 ? string.Empty : $"\nOverride só desta execução: {string.Join(" e ", kept)}.";
    }

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
        JobExecutionContext context, ResolvedRepositoryEnvironment? repositoryEnvironment, AgentModelSelection selection,
        IReadOnlyList<Attachment> images, long userId, long chatId, CancellationToken jobToken,
        CancellationToken stoppingToken)
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
                        repositoryEnvironment?.Values, selection.Model, selection.Effort, images)
                    : await claudeRunner.RunAsync(prompt, context.WorkingDirectory, jobToken,
                        context.Mode == JobExecutionMode.General,
                        repositoryEnvironment?.Values, selection.Model, selection.Effort, images);
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
        ReleaseJobImages(userId, id, images);
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
        (job.ModelSelection?.Model is { } model ? $" | modelo {model}" : string.Empty) +
        $" | esforço {job.ModelSelection?.EffortLabel ?? "padrão da CLI"}" +
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
        $" | modo {AgentSessionModes.Name(session.Profile)} | modelo {session.ModelLabel} | esforço {session.EffortLabel}" +
        $" | criada {session.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";

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
