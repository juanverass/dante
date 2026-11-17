using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Worker.Brain;
using Dante.Worker.Planilhas;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;

namespace Dante.Worker.Sessions;

// In-memory catalog of interactive sessions for the life of the Worker (AD-20). It owns one driver per session,
// routes each user operation to the session it names (or to the user's active session) after checking ownership,
// and pumps driver events into the AgentSession state machine. Agent, context, permission profile and model are fixed
// when the session starts: /use, /agent set or /model never reach a running session. Sessions are not jobs (AD-16) and do not
// survive a Worker restart: stopping the host fails every live session and kills its process. Attachments of a session
// live in its own directory of the attachment store until the session ends (AD-29). With the execution ledger enabled
// (AD-66), the metadata and the key events of each session are mirrored there and the S… number comes from the
// database; the runtime stays here, and the mirror never blocks or fails a session.
public sealed class SessionRegistry(
    IAgentSessionDriverFactory drivers,
    ILogger<SessionRegistry> logger,
    IAgentSessionEventSink? sink = null,
    SessionIdGenerator? ids = null,
    TimeSpan? requestTimeout = null,
    AttachmentStore? attachments = null,
    IAgentToolServers? toolServers = null,
    EspelhoDoLedger? ledger = null) : IAsyncDisposable, IDisposable
{
    private const int RecentEndedLimit = 20;
    private const string RestartNotice = "Sessões existem só em memória e não sobrevivem ao reinício do Worker.";
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> activeSessions = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SessionIdGenerator ids = ids ?? new SessionIdGenerator();
    private readonly TimeSpan requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(5);
    private bool disposed;

    // Starting a session is an explicit selection: on success it becomes the owner's active session.
    public async Task<SessionResult> StartAsync(SessionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Entry? entry = null;
        IAgentSessionDriver driver;
        var reserved = ledger is null ? null : await ledger.ReservarNumeroDeSessaoAsync(ids.LastSessionNumber, cancellationToken);
        lock (gate)
        {
            if (disposed)
            {
                return SessionResult.Reject("O Worker está sendo encerrado; não é possível iniciar sessões.");
            }

            // A registered agent's version is the ceiling of the session (AD-68): checked here, before any driver
            // exists, whatever the caller already checked.
            if (request.RegisteredAgent is { } registeredAgent && RefuseRegistered(request, registeredAgent) is { } refusal)
                return SessionResult.Reject(refusal);

            driver = drivers.Create(request.Agent);
            // A mode the agent cannot map is refused before any process starts (#76).
            if (driver.Capabilities.Modes.Contains(request.Profile))
            {
                var (id, mirrored) = ids.NextSessionId(reserved);
                var session = new AgentSession(id, request.Agent, request.OwnerUserId,
                    request.Context, driver.Capabilities, ids, requestTimeout);
                entry = new Entry(session, driver, request.Profile, request.ModelSelection)
                {
                    LedgerNumber = mirrored ? reserved : null,
                    RegisteredAgent = request.RegisteredAgent,
                    Observer = request.Observer
                };
                sessions.Add(session.Id, entry);
                if (entry.LedgerNumber is { } number)
                {
                    // The agent is frozen in the record too: the registered version, or the built-in materialized from
                    // the preferences this session starts with (AD-68).
                    var agent = request.RegisteredAgent;
                    var registered = new SessaoRegistradaDto(number, request.Agent, LedgerChannel.Telegram,
                        request.OwnerUserId, request.Context, request.Profile, request.ModelSelection?.Model,
                        request.ModelSelection?.Effort, entry.CreatedAtUtc,
                        BootDoWorker: ledger!.Identidade?.Boot, MaquinaDoWorker: ledger.Identidade?.Host,
                        HandleDoAgente: agent?.Handle ?? AgentesEmbutidos.Handle(request.Agent),
                        HashDaConfiguracaoDoAgente: agent?.Versao.HashDaConfiguracao ?? AgentesEmbutidos
                            .Materializar(request.Agent, request.ModelSelection, request.Profile).Hash(),
                        IdVersaoDoAgente: agent?.Versao.Id);
                    ledger!.Registrar($"criação da sessão {id}", (l, token) => l.RegistrarSessaoCriadaAsync(registered, token));
                }
            }
        }

        if (entry is null)
        {
            await driver.DisposeAsync();
            return SessionResult.Reject(
                $"O modo {AgentSessionModes.Name(request.Profile)} não é suportado pelo {request.Agent}. " +
                $"Modos disponíveis: {string.Join(", ", driver.Capabilities.Modes.Select(AgentSessionModes.Name))}.");
        }

        // A directory of a previous run with the same session id must not hand old files to this session.
        ReleaseAttachments(entry);
        try
        {
            var started = await entry.Driver.StartAsync(new AgentSessionStartOptions(
                request.Context.WorkingDirectory,
                request.Context.Mode == JobExecutionMode.General,
                request.EnvironmentVariables,
                request.Profile,
                request.ModelSelection,
                Granted(request, toolServers is null ? null
                    : await toolServers.ForSessionAsync(entry.Session.Id, request, cancellationToken)),
                request.Context.RepositoriosVinculados.Select(linked => linked.Caminho).ToArray(),
                Instructions(request)),
                cancellationToken);
            entry.ReportedModel = started.Model;
            entry.Session.MarkStarted(started);
            if (entry.LedgerNumber is { } number)
            {
                var (upstream, model, at) = (entry.Session.UpstreamSessionId, started.Model, DateTimeOffset.UtcNow);
                var unit = started.UnidadeDeContencao;
                ledger!.Registrar($"início da sessão {entry.Session.Id}", (l, token) =>
                    l.RegistrarSessaoIniciadaAsync(number, upstream, model, at, token, unit));
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao iniciar a sessão {SessionId} de {Agent} ({ErrorType}).", entry.Session.Id,
                request.Agent, exception.GetType().Name);
            await FailAsync(entry, exception is AgentProtocolException
                ? exception.Message
                : $"Não foi possível iniciar a sessão do {request.Agent}.");
            await entry.DisposeDriverAsync();
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return new SessionResult(false, Snapshot(entry), entry.Session.Error);
        }

        entry.Pump = Task.Run(() => PumpAsync(entry));
        // A reserved session (task step, AD-68) is never the owner's active one: ordinary messages keep their route.
        if (request.Observer is null)
        {
            lock (gate)
            {
                activeSessions[request.OwnerUserId] = entry.Session.Id;
            }
        }

        // Messages sent to the session id while it was starting are waiting in its queue.
        await StartNextQueuedAsync(entry, cancellationToken);
        return await EndIfFailedAsync(entry);
    }

    public async Task<SessionResult> ChangeModeAsync(long userId, string? sessionId,
        AgentPermissionProfile profile, CancellationToken cancellationToken = default)
    {
        var entry = Find(userId, sessionId, out var error);
        if (entry is null) return SessionResult.Reject(error!);
        await entry.Upstream.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Snapshot(entry);
            if (snapshot.Compacting) return SessionResult.Reject(CompactingRefusal(snapshot.Id));
            if (snapshot.State != AgentSessionState.Idle || snapshot.QueuedCount != 0 || snapshot.PendingRequestIds.Count != 0)
                return SessionResult.Reject("Não é possível trocar o modo enquanto há um turno ou solicitação pendente. " +
                    "Aguarde a conclusão ou use /session stop para interromper explicitamente.");
            if (!entry.Driver.Capabilities.Modes.Contains(profile))
                return SessionResult.Reject($"O modo {AgentSessionModes.Name(profile)} não é suportado pelo {snapshot.Agent}.");
            // A session never goes past the maximum of its agent's version (AD-68).
            if (entry.RegisteredAgent is { } registered && !registered.PermitePerfil(profile))
                return SessionResult.Reject(AboveMaximum(registered, profile));
            if (entry.Driver.Capabilities.ModeSwitch == AgentModeSwitch.Unsupported)
                return SessionResult.Reject($"Esta sessão {snapshot.Agent} não permite trocar o modo sem reiniciar. " +
                    $"Use /session start {snapshot.Agent.ToString().ToLowerInvariant()} {AgentSessionModes.Name(profile)} para abrir uma nova sessão.");
            if (snapshot.Profile == profile && snapshot.PendingProfile is null)
                return new SessionResult(true, snapshot);
            try
            {
                await entry.Driver.ChangeModeAsync(profile, cancellationToken);
                lock (gate)
                {
                    if (entry.Driver.Capabilities.ModeSwitch == AgentModeSwitch.NextTurn)
                        entry.PendingProfile = profile == entry.Profile ? null : profile;
                    else
                    {
                        entry.Profile = profile;
                        entry.PendingProfile = null;
                    }
                }
                return new SessionResult(true, Snapshot(entry));
            }
            catch (Exception exception)
            {
                logger.LogWarning("Troca de modo recusada na sessão {SessionId} ({ErrorType}).", snapshot.Id,
                    exception.GetType().Name);
                if (exception is AgentModeUnconfirmedException or OperationCanceledException or TimeoutException)
                {
                    await FailAsync(entry, "O agente não confirmou o modo; a sessão foi encerrada para evitar políticas divergentes. Inicie uma nova sessão.");
                    await entry.DisposeDriverAsync();
                    return new SessionResult(false, Snapshot(entry), entry.Session.Error);
                }
                return new SessionResult(false, Snapshot(entry),
                    "Não foi possível confirmar a troca de modo. O modo anterior foi mantido; tente novamente ou inicie uma nova sessão.");
            }
        }
        finally { entry.Upstream.Release(); }
    }

    // /clear (#120, AD-32): the same session, process, directory, model, effort and mode with an empty upstream
    // conversation. Idle only, serialized with turns, answers, mode changes and close. A refusal keeps the previous
    // conversation; an unconfirmed result fails the session instead of letting turns reach an unknown context.
    public async Task<SessionResult> ClearContextAsync(long userId, string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(userId, sessionId, out var error);
        if (entry is null) return SessionResult.Reject(error!);
        if (!entry.Driver.Capabilities.ClearContext)
            return SessionResult.Reject($"O {entry.Session.Agent} não oferece limpeza da conversa nesta sessão.");
        await entry.Upstream.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Snapshot(entry);
            if (snapshot.Compacting) return SessionResult.Reject(CompactingRefusal(snapshot.Id));
            if (snapshot.State != AgentSessionState.Idle || snapshot.QueuedCount != 0 ||
                snapshot.PendingRequestIds.Count != 0 || snapshot.PendingProfile is not null)
            {
                return SessionResult.Reject(snapshot.PendingProfile is not null
                    ? "Há uma troca de modo aguardando o próximo turno do Codex; envie uma mensagem para aplicá-la ou " +
                      "volte ao modo atual e então use /clear."
                    : "Só é possível limpar a conversa com a sessão ociosa. Aguarde o turno, a fila e as solicitações " +
                      "pendentes terminarem, ou use /session stop para interromper explicitamente.");
            }

            try
            {
                var cleared = await entry.Driver.ClearContextAsync(cancellationToken);
                entry.Session.ReplaceUpstream(cleared.UpstreamSessionId);
                lock (gate)
                {
                    entry.LastTurnOutcome = null;
                    entry.TurnsSinceContextReset = 0;
                }
            }
            catch (AgentContextUnchangedException exception)
            {
                return new SessionResult(false, Snapshot(entry), exception.Message);
            }
            catch (Exception exception)
            {
                logger.LogWarning("Limpeza não confirmada na sessão {SessionId} ({ErrorType}).", snapshot.Id,
                    exception.GetType().Name);
                await FailAsync(entry, "O agente não confirmou a limpeza da conversa; a sessão foi encerrada para não " +
                    "continuar num contexto incerto. Inicie outra com /session start.");
                await entry.DisposeDriverAsync();
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                return new SessionResult(false, Snapshot(entry), entry.Session.Error);
            }

            // Images of the previous conversation are not part of the new one (a later /vitrine must not reuse them).
            ReleaseAttachments(entry);
            return new SessionResult(true, Snapshot(entry));
        }
        finally { entry.Upstream.Release(); }
    }

    // /compact (#121, AD-32): accepted only for the owner's idle session that has a turn since it started or was
    // cleared. The compaction then runs in the background, so a long one never holds the session lock: meanwhile messages,
    // mode changes and clears are refused, /session stop cancels it upstream and close ends it. Completion says how it
    // ended: confirmed, refused with the history intact, or uncertain (the session is failed).
    public async Task<SessionCompaction> CompactContextAsync(long userId, string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(userId, sessionId, out var error);
        if (entry is null) return new SessionCompaction(SessionResult.Reject(error!));
        if (!entry.Driver.Capabilities.CompactContext)
            return new SessionCompaction(SessionResult.Reject(
                $"O {entry.Session.Agent} não oferece compactação da conversa nesta sessão."));
        CompactionRun run;
        await entry.Upstream.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Snapshot(entry);
            if (snapshot.Compacting) return new SessionCompaction(SessionResult.Reject(CompactingRefusal(snapshot.Id)));
            if (snapshot.State != AgentSessionState.Idle || snapshot.QueuedCount != 0 ||
                snapshot.PendingRequestIds.Count != 0 || snapshot.PendingProfile is not null)
            {
                return new SessionCompaction(SessionResult.Reject(snapshot.PendingProfile is not null
                    ? "Há uma troca de modo aguardando o próximo turno do Codex; envie uma mensagem para aplicá-la e " +
                      "então use /compact."
                    : "Só é possível compactar a conversa com a sessão ociosa. Aguarde o turno, a fila e as solicitações " +
                      "pendentes terminarem, ou use /session stop para interromper explicitamente."));
            }

            lock (gate)
            {
                // Nothing to summarize: the Codex would still "compact" an empty thread (#119), so it is not asked.
                if (entry.TurnsSinceContextReset == 0)
                    return new SessionCompaction(SessionResult.Reject(
                        "Nada a compactar: a conversa desta sessão ainda não tem nenhuma resposta do agente."));
                run = new CompactionRun(CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
                entry.Compaction = run;
            }
        }
        finally { entry.Upstream.Release(); }

        var completion = Task.Run(() => RunCompactionAsync(entry, run));
        return new SessionCompaction(new SessionResult(true, Snapshot(entry)), completion);
    }

    private async Task<SessionCompactionResult> RunCompactionAsync(Entry entry, CompactionRun run)
    {
        SessionCompactionResult outcome;
        try
        {
            var compacted = await entry.Driver.CompactContextAsync(run.Cancel.Token);
            outcome = new SessionCompactionResult(true, PreTokens: compacted.PreTokens, PostTokens: compacted.PostTokens);
        }
        catch (AgentContextUnchangedException exception)
        {
            outcome = new SessionCompactionResult(false, Error: exception.Message);
        }
        catch (Exception) when (IsEnded(entry.Session.State))
        {
            outcome = new SessionCompactionResult(false, Error: "A sessão foi encerrada durante a compactação.");
        }
        catch (Exception exception)
        {
            logger.LogWarning("Compactação não confirmada na sessão {SessionId} ({ErrorType}).", entry.Session.Id,
                exception.GetType().Name);
            await FailAsync(entry, "O agente não confirmou a compactação; a sessão foi encerrada para não continuar " +
                "num contexto incerto. Inicie outra com /session start.");
            await entry.DisposeDriverAsync();
            outcome = new SessionCompactionResult(false, Error: entry.Session.Error);
        }
        finally
        {
            lock (gate)
            {
                if (entry.Compaction == run) entry.Compaction = null;
            }

            run.Cancel.Dispose();
        }

        return outcome with { Session = Snapshot(entry) };
    }

    private static string CompactingRefusal(string sessionId) =>
        $"A conversa da sessão {sessionId} está sendo compactada. Aguarde o aviso de conclusão ou use /session stop " +
        "para cancelar a compactação.";

    // sessionId null routes to the user's active session.
    public Task<SessionSubmitResult> SubmitAsync(long userId, string? sessionId, AgentInput input,
        MessageDelivery delivery = MessageDelivery.Queue, CancellationToken cancellationToken = default) =>
        SubmitCoreAsync(userId, sessionId, input, delivery, reserved: false, cancellationToken);

    // Task step (AD-68): only the executor that started the reserved session sends its turn.
    internal Task<SessionSubmitResult> SubmitReservedAsync(long ownerUserId, string sessionId, AgentInput input,
        CancellationToken cancellationToken = default) =>
        SubmitCoreAsync(ownerUserId, sessionId, input, MessageDelivery.Queue, reserved: true, cancellationToken);

    // Task step (#305): the ledger number (S…) of a reserved session, once its creation record is written, so the step
    // can point to it. Null when the session is not mirrored (no ledger, or the number came from the local counter).
    internal async Task<long?> LedgerNumberWrittenAsync(long ownerUserId, string sessionId,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(ownerUserId, sessionId, out _, reserved: true);
        if (entry?.LedgerNumber is not { } number || ledger is null) return null;
        // The mirror writes in order: once flushed, the creation record queued at start is in the ledger (or failed).
        await ledger.DescarregarAsync(cancellationToken);
        return number;
    }

    private async Task<SessionSubmitResult> SubmitCoreAsync(long userId, string? sessionId, AgentInput input,
        MessageDelivery delivery, bool reserved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
        var entry = Find(userId, sessionId, out var error, reserved);
        if (entry is null)
        {
            return SessionSubmitResult.Reject(error!, sessionId);
        }

        // A compaction in progress owns the upstream conversation (#121): nothing is sent or queued meanwhile.
        if (entry.Compaction is not null)
        {
            return SessionSubmitResult.Reject(CompactingRefusal(entry.Session.Id), entry.Session.Id);
        }

        // Refused before the state machine sees it (#128): no turn opens, no turn is interrupted, nothing is queued.
        if (entry.Driver.Capabilities.CommandsInText && AgentInput.StartsWithCommand(input.Text))
        {
            return SessionSubmitResult.Reject(AgentInput.CommandRefusal(entry.Session.Agent), entry.Session.Id);
        }

        // An attachment the agent cannot receive is refused, never reduced to its file name (AD-29).
        if (input.Attachments.Any(attachment => attachment.OwnerId != userId))
        {
            return SessionSubmitResult.Reject("Anexo de outro usuário.", entry.Session.Id);
        }

        // Audio and video arrive only through the media preparation of a new turn (#96): never in a steer, which
        // reaches a turn already running upstream.
        var media = MediaPreparer.NeedsPreparation(input.Attachments);
        if (input.Attachments.Any(attachment => attachment.Kind == AttachmentKind.Document) ||
            media && !entry.Driver.Capabilities.MediaInput)
        {
            return SessionSubmitResult.Reject("Só imagens chegam ao agente; áudio, vídeo e outros arquivos ainda não.",
                entry.Session.Id);
        }

        if (media && delivery == MessageDelivery.Steer)
        {
            return SessionSubmitResult.Reject("Áudio e vídeo não vão em /steer; envie como mensagem comum, que entra " +
                "na fila da sessão.", entry.Session.Id);
        }

        // Images and video frames need image input; an audio transcript is plain text.
        if (input.Attachments.Any(attachment => attachment.Kind != AttachmentKind.Audio) &&
            !entry.Driver.Capabilities.ImageInput)
        {
            return SessionSubmitResult.Reject(
                $"O {entry.Session.Agent} não recebe imagens nesta sessão; a mensagem não foi enviada.", entry.Session.Id);
        }

        // SteerByInterrupt interrupts the turn upstream: serialized with the other request operations.
        await entry.Upstream.WaitAsync(cancellationToken);
        try
        {
            // Compaction may have acquired the semaphore first while this message was waiting.
            if (entry.Compaction is not null)
                return SessionSubmitResult.Reject(CompactingRefusal(entry.Session.Id), entry.Session.Id);
            return await SubmitLockedAsync(entry, input, delivery, cancellationToken);
        }
        finally
        {
            entry.Upstream.Release();
        }
    }

    private async Task<SessionSubmitResult> SubmitLockedAsync(Entry entry, AgentInput input, MessageDelivery delivery,
        CancellationToken cancellationToken)
    {
        var session = entry.Session;
        var result = session.Submit(input, delivery);
        try
        {
            switch (result.Outcome)
            {
                case SubmitOutcome.TurnStarted:
                    toolServers?.BeginTurn(session.Id, input);
                    await entry.Driver.StartTurnAsync(input, cancellationToken);
                    break;
                case SubmitOutcome.Steered:
                    await entry.Driver.SteerAsync(input, cancellationToken);
                    toolServers?.BeginTurn(session.Id, input); // Só a confirmação do driver substitui a evidência anterior.
                    break;
                case SubmitOutcome.SteerByInterrupt:
                    await entry.Driver.InterruptTurnAsync(cancellationToken);
                    break;
            }
        }
        catch (AgentInputRejectedException exception) when (result.Outcome == SubmitOutcome.TurnStarted)
        {
            // The driver wrote nothing: the turn opened here ends without reaching the agent, and the session stays usable.
            var completed = session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Failed, exception.Message));
            await PublishAsync(entry, completed);
            return SessionSubmitResult.Reject(exception.Message, session.Id);
        }
        catch (AgentModeRejectedException exception)
        {
            lock (gate) entry.PendingProfile = null;
            // A rejected turn/start never reached the agent. Keep the upstream thread and the previous profile.
            var completed = session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Failed, exception.Message));
            await PublishAsync(entry, completed);
            return SessionSubmitResult.Reject(exception.Message, session.Id);
        }
        catch (AgentSteerRejectedException exception) when (result.Outcome == SubmitOutcome.Steered)
        {
            return SessionSubmitResult.Reject(exception.Message, session.Id);
        }
        catch (Exception exception) when (result.Outcome == SubmitOutcome.Steered)
        {
            if (exception is OperationCanceledException)
            {
                throw;
            }

            // The turn may have ended between Submit and turn/steer; the session itself is still usable.
            logger.LogWarning("Steer recusado na sessão {SessionId} ({ErrorType}).", session.Id,
                exception.GetType().Name);
            return SessionSubmitResult.Reject($"Não foi possível orientar o turno {result.TurnId}.", session.Id);
        }
        catch (Exception exception)
        {
            // A turn that was opened but never reached the agent would stay Running forever.
            logger.LogWarning("Falha ao enviar mensagem à sessão {SessionId} ({ErrorType}).", session.Id,
                exception.GetType().Name);
            await FailAsync(entry, entry.PendingProfile is not null
                ? "Não foi possível confirmar as políticas da troca de modo; a sessão foi encerrada. Atualize a CLI ou inicie uma nova sessão explicitamente."
                : "Não foi possível enviar a mensagem ao agente; a sessão foi encerrada.");
            await entry.DisposeDriverAsync();
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return SessionSubmitResult.Reject(session.Error!, session.Id);
        }

        return new SessionSubmitResult(result.Outcome, session.Id, result.TurnId, result.Error);
    }

    // Stop turn (AD-16): pending requests expire and the queue is discarded; the session goes back to Idle.
    // During a compaction (#121) it cancels the compaction instead; the driver interrupts it upstream.
    public async Task<SessionResult> InterruptAsync(long userId, string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(userId, sessionId, out var error);
        if (entry is null)
        {
            return SessionResult.Reject(error!);
        }

        if (CancelCompaction(entry))
        {
            // What was stopped, even if the cancelled compaction already ended while this answer is built.
            return new SessionResult(true, Snapshot(entry) with { Compacting = true });
        }

        await entry.Upstream.WaitAsync(cancellationToken);
        try
        {
            return await InterruptLockedAsync(entry, cancellationToken);
        }
        finally
        {
            entry.Upstream.Release();
        }
    }

    private bool CancelCompaction(Entry entry)
    {
        CompactionRun? run;
        lock (gate) run = entry.Compaction;
        if (run is null) return false;
        try { run.Cancel.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    private async Task<SessionResult> InterruptLockedAsync(Entry entry, CancellationToken cancellationToken)
    {
        var inputs = PendingInputs(entry);
        if (!entry.Session.TryInterrupt(out var discarded))
        {
            return SessionResult.Reject($"A sessão {entry.Session.Id} não tem turno em andamento para interromper.");
        }

        await PublishClosedInputsAsync(entry, inputs);

        try
        {
            await entry.Driver.InterruptTurnAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao interromper a sessão {SessionId} ({ErrorType}).", entry.Session.Id,
                exception.GetType().Name);
            await FailAsync(entry, "Não foi possível interromper o turno; a sessão foi encerrada.");
            await entry.DisposeDriverAsync();
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return new SessionResult(false, Snapshot(entry), entry.Session.Error);
        }

        return new SessionResult(true, Snapshot(entry), DiscardedMessages: discarded);
    }

    public Task<SessionResult> CloseAsync(long userId, string? sessionId,
        CancellationToken cancellationToken = default) =>
        CloseCoreAsync(userId, sessionId, reserved: false, cancellationToken);

    // Task step (AD-68): the executor closes the reserved session it started, whatever the turn did.
    internal Task<SessionResult> CloseReservedAsync(long ownerUserId, string sessionId,
        CancellationToken cancellationToken = default) =>
        CloseCoreAsync(ownerUserId, sessionId, reserved: true, cancellationToken);

    private async Task<SessionResult> CloseCoreAsync(long userId, string? sessionId, bool reserved,
        CancellationToken cancellationToken)
    {
        var entry = Find(userId, sessionId, out var error, reserved);
        if (entry is null)
        {
            return SessionResult.Reject(error!);
        }

        CancelCompaction(entry);

        await entry.Upstream.WaitAsync(cancellationToken);
        try
        {
            return await CloseLockedAsync(userId, entry, cancellationToken);
        }
        finally
        {
            entry.Upstream.Release();
        }
    }

    private async Task<SessionResult> CloseLockedAsync(long userId, Entry entry, CancellationToken cancellationToken)
    {
        var session = entry.Session;
        var hadTurn = session.ActiveTurnId is not null;
        var inputs = PendingInputs(entry);
        if (!session.TryClose())
        {
            return SessionResult.Reject($"A sessão {session.Id} já está sendo encerrada.");
        }

        // This close holds Upstream and waits for the pump below. A pump that has just applied a turn completion may be
        // about to take Upstream to start the next queued message: cancelling its wait lets it end instead of waiting
        // for this close forever (a deadlock since #65, made likely by a task step closing right after its turn).
        await entry.Closing.CancelAsync();

        await PublishClosedInputsAsync(entry, inputs);

        lock (gate)
        {
            if (activeSessions.TryGetValue(userId, out var active) && active == session.Id)
            {
                activeSessions.Remove(userId);
            }
        }

        try
        {
            if (hadTurn)
            {
                await entry.Driver.InterruptTurnAsync(cancellationToken);
            }

            await entry.Driver.CloseAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Closing anyway: disposing the driver below kills the process tree.
            logger.LogWarning("Falha ao encerrar a sessão {SessionId} normalmente ({ErrorType}).", session.Id,
                exception.GetType().Name);
        }
        finally
        {
            await entry.DisposeDriverAsync();
            if (entry.Pump is { } pump)
            {
                await pump;
            }

            if (session.State == AgentSessionState.Closing)
            {
                toolServers?.EndSession(session.Id);
                session.MarkClosed();
            }

            MarkEnded(entry);
            ReleaseAttachments(entry);
        }

        return new SessionResult(true, Snapshot(entry));
    }

    // Routes an approval or input answer to the session whose turn raised the request; only the owner may answer.
    public Task<SessionResult> RespondAsync(long userId, string requestId, AgentUserResponse response,
        CancellationToken cancellationToken = default) =>
        RespondCoreAsync(userId, requestId, response, reserved: false, cancellationToken);

    // Task step (AD-68): requests of a reserved session are answered only by its executor.
    internal Task<SessionResult> RespondReservedAsync(long ownerUserId, string requestId, AgentUserResponse response,
        CancellationToken cancellationToken = default) =>
        RespondCoreAsync(ownerUserId, requestId, response, reserved: true, cancellationToken);

    private async Task<SessionResult> RespondCoreAsync(long userId, string requestId, AgentUserResponse response,
        bool reserved, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(response);
        Entry? entry;
        lock (gate)
        {
            entry = sessions.Values.FirstOrDefault(candidate =>
                candidate.Session.PendingRequestIds.Contains(requestId, StringComparer.OrdinalIgnoreCase) &&
                (candidate.Observer is null) != reserved);
        }

        if (entry is null)
        {
            return SessionResult.Reject($"A solicitação {requestId} não está pendente.");
        }

        await entry.Upstream.WaitAsync(cancellationToken);
        try
        {
            return await RespondLockedAsync(entry, userId, requestId, response, cancellationToken);
        }
        finally
        {
            entry.Upstream.Release();
        }
    }

    private async Task<SessionResult> RespondLockedAsync(Entry entry, long userId, string requestId,
        AgentUserResponse response, CancellationToken cancellationToken)
    {
        var resolution = entry.Session.Resolve(requestId, userId, response);
        if (!resolution.Accepted)
        {
            return SessionResult.Reject(resolution.Error!);
        }

        try
        {
            await entry.Driver.RespondAsync(resolution.UpstreamRequestId!, response, cancellationToken);
            AgentEvent resolved = response is AgentApprovalResponse approval
                ? new RequestResolvedEvent(requestId, approval.Decision) : new InputResolvedEvent(requestId);
            await PublishAsync(entry, resolved with
            {
                SessionId = entry.Session.Id,
                TurnId = resolution.TurnId,
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao responder a solicitação {RequestId} da sessão {SessionId} ({ErrorType}).",
                requestId, entry.Session.Id, exception.GetType().Name);
            // Resolve consumed the request. If delivery failed, the agent may wait forever for an answer.
            await FailAsync(entry, $"Não foi possível entregar a resposta de {requestId} ao agente; a sessão foi encerrada.");
            await entry.DisposeDriverAsync();
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return new SessionResult(false, Snapshot(entry), entry.Session.Error);
        }

        return new SessionResult(true, Snapshot(entry));
    }

    public AgentPendingRequest? GetPendingRequest(long userId, string requestId)
    {
        lock (gate)
        {
            return sessions.Values.Where(candidate => candidate.Session.OwnerUserId == userId)
                .Select(candidate => candidate.Session.GetPendingRequest(requestId))
                .FirstOrDefault(request => request is not null);
        }
    }

    private static AgentPendingRequest[] PendingInputs(Entry entry) => entry.Session.PendingRequestIds
        .Select(entry.Session.GetPendingRequest).OfType<AgentPendingRequest>().Where(request => !request.IsApproval).ToArray();

    private async Task PublishClosedInputsAsync(Entry entry, IEnumerable<AgentPendingRequest> inputs)
    {
        foreach (var request in inputs)
            await PublishAsync(entry, new RequestClosedEvent(request.RequestId)
            {
                SessionId = request.SessionId, TurnId = request.TurnId, TimestampUtc = DateTimeOffset.UtcNow
            });
    }

    public Task<SessionResult> RespondAsync(long userId, string sessionId, string turnId,
        string requestId, AgentUserResponse response, CancellationToken cancellationToken = default)
    {
        var pending = GetPendingRequest(userId, requestId);
        if (pending is null || !pending.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase) ||
            !pending.TurnId.Equals(turnId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(SessionResult.Reject("Solicitação não encontrada neste turno da sessão."));
        return RespondAsync(userId, requestId, response, cancellationToken);
    }

    // Explicit selection of the active session; null clears it (the next plain message opens a new session, AD-23).
    public SessionResult Select(long userId, string? sessionId)
    {
        if (sessionId is null)
        {
            lock (gate)
            {
                activeSessions.Remove(userId);
            }

            return new SessionResult(true);
        }

        var entry = Find(userId, sessionId, out var error);
        if (entry is null)
        {
            return SessionResult.Reject(error!);
        }

        lock (gate)
        {
            activeSessions[userId] = entry.Session.Id;
            return new SessionResult(true, Snapshot(entry));
        }
    }

    public AgentSessionSnapshot? GetActive(long userId)
    {
        lock (gate)
        {
            return activeSessions.TryGetValue(userId, out var id) && sessions.TryGetValue(id, out var entry)
                ? Snapshot(entry)
                : null;
        }
    }

    // Only the owner's sessions: live ones and the most recently ended.
    // Live sessions of one registered agent, of every owner (AD-68): what keeps running after it is deactivated.
    public IReadOnlyList<AgentSessionSnapshot> ListLiveOfRegisteredAgent(string handle)
    {
        lock (gate)
        {
            return sessions.Values
                .Where(entry => entry.RegisteredAgent?.Handle == handle && !IsEnded(entry.Session.State))
                .Select(Snapshot)
                .ToArray();
        }
    }

    public IReadOnlyList<AgentSessionSnapshot> List(long userId)
    {
        lock (gate)
        {
            return sessions.Values
                .Where(entry => entry.Session.OwnerUserId == userId)
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .ThenByDescending(entry => entry.Session.Id, StringComparer.Ordinal)
                .Select(Snapshot)
                .ToArray();
        }
    }

    // Host shutdown: every live session fails explicitly and its process tree is killed.
    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            entries = sessions.Values.ToArray();
        }

        await lifetime.CancelAsync();
        foreach (var entry in entries)
        {
            await FailAsync(entry, "O Worker foi encerrado. " + RestartNotice);
            await entry.DisposeDriverAsync();
            if (entry.Pump is { } pump)
            {
                await pump;
            }
        }
    }

    // Host.Run disposes its service provider synchronously.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task PumpAsync(Entry entry)
    {
        var session = entry.Session;
        try
        {
            await foreach (var agentEvent in entry.Driver.ReadEventsAsync(lifetime.Token))
            {
                if (agentEvent is ModeAppliedEvent mode)
                {
                    lock (gate)
                    {
                        if (entry.PendingProfile == mode.Profile && !IsEnded(session.State))
                        {
                            entry.Profile = mode.Profile;
                            entry.PendingProfile = null;
                        }
                    }
                    continue;
                }
                AgentEvent stamped;
                try
                {
                    stamped = session.Apply(agentEvent);
                }
                catch (InvalidOperationException) when (!IsEnded(session.State))
                {
                    logger.LogWarning("Evento {EventType} sem turno ativo ignorado na sessão {SessionId}.",
                        agentEvent.GetType().Name, session.Id);
                    continue;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (stamped is TurnCompletedEvent completed)
                {
                    lock (gate)
                    {
                        entry.LastTurnOutcome = completed.Outcome;
                        entry.TurnsSinceContextReset++;
                    }
                }
                if (stamped is ApprovalRequestedEvent { RequestId: var approvalId })
                    _ = ExpireRequestAsync(entry, approvalId, true);
                else if (stamped is UserInputRequestedEvent { RequestId: var inputId })
                    _ = ExpireRequestAsync(entry, inputId, false);
                await PublishAsync(entry, stamped);
                if (stamped is TurnCompletedEvent)
                {
                    using var next = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, entry.Closing.Token);
                    await StartNextQueuedAsync(entry, next.Token);
                }

                if (session.State == AgentSessionState.Failed)
                {
                    break;
                }
            }

            if (!IsEnded(session.State))
            {
                await FailAsync(entry, "O processo do agente encerrou inesperadamente.");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Worker shutdown: DisposeAsync fails the session.
        }
        catch (Exception exception) when (session.State is AgentSessionState.Closing or AgentSessionState.Closed)
        {
            // Closing kills the process; a broken stream at that point is expected.
            logger.LogDebug("Fluxo da sessão {SessionId} terminou durante o encerramento ({ErrorType}).", session.Id,
                exception.GetType().Name);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha no fluxo de eventos da sessão {SessionId} ({ErrorType}).", session.Id,
                exception.GetType().Name);
            await FailAsync(entry, exception is AgentProtocolException
                ? exception.Message
                : "Falha inesperada na sessão do agente.");
        }

        if (session.State == AgentSessionState.Failed)
        {
            await entry.DisposeDriverAsync();
        }
    }

    private async Task ExpireRequestAsync(Entry entry, string requestId, bool isApproval)
    {
        try
        {
            // A timer may fire slightly before the wall clock reaches the deadline, and TryExpire would then refuse
            // and leave the request pending forever: wait until the deadline really passed.
            while (entry.Session.GetPendingRequest(requestId) is { } pending &&
                   pending.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                await Task.Delay(pending.ExpiresAtUtc - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1),
                    lifetime.Token);
            }

            RequestResolution resolution;
            // Expiring and answering the upstream request is one step: a stop or close cannot clear the upstream
            // request between them, so its absence is never mistaken for a broken session.
            await entry.Upstream.WaitAsync(lifetime.Token);
            try
            {
                resolution = entry.Session.TryExpire(requestId);
                if (!resolution.Accepted) return;
                AgentUserResponse response = isApproval
                    ? new AgentApprovalResponse(AgentApprovalDecision.Deny, "Solicitação expirada.")
                    : new AgentInputResponse(new Dictionary<string, string>());
                await entry.Driver.RespondAsync(resolution.UpstreamRequestId!, response, lifetime.Token);
            }
            finally
            {
                entry.Upstream.Release();
            }

            await PublishAsync(entry, new RequestExpiredEvent(requestId)
            {
                SessionId = entry.Session.Id,
                TurnId = resolution.TurnId,
                TimestampUtc = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao expirar a solicitação {RequestId} ({ErrorType}).",
                requestId, exception.GetType().Name);
            await FailAsync(entry, $"Não foi possível expirar a solicitação {requestId}; a sessão foi encerrada.");
            await entry.DisposeDriverAsync();
        }
    }

    private async Task StartNextQueuedAsync(Entry entry, CancellationToken cancellationToken)
    {
        await entry.Upstream.WaitAsync(cancellationToken);
        try
        {
            await StartNextQueuedLockedAsync(entry, cancellationToken);
        }
        finally { entry.Upstream.Release(); }
    }

    private async Task StartNextQueuedLockedAsync(Entry entry, CancellationToken cancellationToken)
    {
        while (entry.Session.TryStartQueued(out _, out var input))
        {
            try
            {
                toolServers?.BeginTurn(entry.Session.Id, input!);
                await entry.Driver.StartTurnAsync(input!, cancellationToken);
                return;
            }
            catch (AgentInputRejectedException exception)
            {
                // Refused by the driver before writing: that message ends as a failed turn and the next one goes on.
                await PublishAsync(entry, entry.Session.Apply(new TurnCompletedEvent(AgentTurnOutcome.Failed,
                    exception.Message)));
            }
            catch (Exception exception)
            {
                logger.LogWarning("Falha ao iniciar turno enfileirado na sessão {SessionId} ({ErrorType}).",
                    entry.Session.Id, exception.GetType().Name);
                await FailAsync(entry, "Não foi possível enviar a mensagem ao agente; a sessão foi encerrada.");
                return;
            }
        }
    }

    private async Task<SessionResult> EndIfFailedAsync(Entry entry)
    {
        if (entry.Session.State != AgentSessionState.Failed)
        {
            return new SessionResult(true, Snapshot(entry));
        }

        await entry.DisposeDriverAsync();
        return new SessionResult(false, Snapshot(entry), entry.Session.Error);
    }

    // Terminal failure; the caller disposes the driver (never from inside the event loop that drains it).
    private async Task FailAsync(Entry entry, string error)
    {
        toolServers?.EndSession(entry.Session.Id);
        entry.Session.MarkFailed(error);
        if (entry.Session.State != AgentSessionState.Failed || !MarkEnded(entry))
        {
            return;
        }

        ReleaseAttachments(entry);

        await PublishAsync(entry, new ErrorEvent(entry.Session.Error!)
        {
            SessionId = entry.Session.Id,
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    private async Task PublishAsync(Entry entry, AgentEvent agentEvent)
    {
        if (entry.LedgerNumber is { } number && ProjecaoDeEventosDaSessao.Projetar(number, agentEvent) is { } projected)
            ledger!.Registrar($"{projected.Tipo} da sessão {entry.Session.Id}", (l, token) =>
                l.RegistrarEventoDaSessaoAsync(projected, token));
        // A reserved session talks only to its executor (AD-68): nothing of the task reaches Telegram before the
        // result is recorded under the queue's lease.
        var target = entry.Observer ?? sink;
        if (target is null)
        {
            return;
        }

        try
        {
            await target.PublishAsync(Snapshot(entry), agentEvent, lifetime.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao publicar evento {EventType} da sessão {SessionId} ({ErrorType}).",
                agentEvent.GetType().Name, entry.Session.Id, exception.GetType().Name);
        }
    }

    // reserved: the caller is the executor of a reserved session (AD-68). The owner's commands never reach one.
    private Entry? Find(long userId, string? sessionId, out string? error, bool reserved = false)
    {
        lock (gate)
        {
            if (sessionId is null && !activeSessions.TryGetValue(userId, out sessionId))
            {
                error = "Nenhuma sessão ativa. Inicie ou selecione uma sessão.";
                return null;
            }

            // Another user's session is reported exactly like a missing one.
            if (!sessions.TryGetValue(sessionId, out var entry) || entry.Session.OwnerUserId != userId)
            {
                error = $"Sessão {sessionId} não encontrada. {RestartNotice}";
                return null;
            }

            if ((entry.Observer is not null) != reserved)
            {
                error = reserved ? $"A sessão {sessionId} não é reservada."
                    : $"A sessão {entry.Session.Id} executa uma tarefa e não recebe comandos.";
                return null;
            }

            var session = entry.Session;
            if (IsEnded(session.State))
            {
                error = $"A sessão {session.Id} está encerrada ({session.State})" +
                        (session.Error is null ? "." : $": {session.Error}");
                return null;
            }

            error = null;
            return entry;
        }
    }

    // The files of an ended session are no longer needed by its agent: queued or sent, they go away with it.
    private void ReleaseAttachments(Entry entry)
    {
        try
        {
            attachments?.DeleteScope(entry.Session.OwnerUserId, entry.Session.Id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leftovers are removed by the startup sweep.
            logger.LogWarning("Falha ao apagar os anexos da sessão {SessionId} ({ErrorType}).", entry.Session.Id,
                exception.GetType().Name);
        }
    }

    private bool MarkEnded(Entry entry)
    {
        lock (gate)
        {
            if (entry.EndedAtUtc is not null)
            {
                return false;
            }

            entry.PendingProfile = null;
            entry.EndedAtUtc = DateTimeOffset.UtcNow;
            if (entry.LedgerNumber is { } number)
            {
                // A session failed by the host shutdown was interrupted by the restart, not by the agent.
                var session = entry.Session;
                var end = new SessaoEncerradaDto(number, session.State switch
                {
                    AgentSessionState.Failed when disposed => EstadoDaSessaoDeAgente.Interrompida,
                    AgentSessionState.Failed => EstadoDaSessaoDeAgente.Falhou,
                    _ => EstadoDaSessaoDeAgente.Encerrada
                }, session.Error, session.UpstreamSessionId, entry.EndedAtUtc.Value);
                ledger!.Registrar($"fim da sessão {session.Id}", (l, token) => l.RegistrarSessaoEncerradaAsync(end, token));
            }
            var ended = sessions.Values
                .Where(candidate => candidate.EndedAtUtc is not null)
                .OrderByDescending(candidate => candidate.EndedAtUtc)
                .ThenByDescending(candidate => candidate.Session.Id, StringComparer.Ordinal)
                .Skip(RecentEndedLimit);
            foreach (var old in ended.ToArray())
            {
                sessions.Remove(old.Session.Id);
            }

            return true;
        }
    }

    private AgentSessionSnapshot Snapshot(Entry entry)
    {
        lock (gate)
        {
            var session = entry.Session;
            return new AgentSessionSnapshot(session.Id, session.Agent, session.OwnerUserId, session.Context,
                entry.Profile, session.State, session.ActiveTurnId, session.QueuedCount, session.PendingRequestIds,
                activeSessions.TryGetValue(session.OwnerUserId, out var active) && active == session.Id,
                entry.CreatedAtUtc, entry.EndedAtUtc, session.Error, entry.LastTurnOutcome, entry.ModelSelection,
                entry.ReportedModel)
            {
                PendingProfile = entry.PendingProfile, UpstreamSessionId = session.UpstreamSessionId,
                Compacting = entry.Compaction is not null, RegisteredAgent = entry.RegisteredAgent
            };
        }
    }

    // Caller holds the gate. Profile, driver, repositories and the agent's simultaneous limit (AD-68).
    private string? RefuseRegistered(SessionStartRequest request, AgenteDaSessao registered)
    {
        var configuration = registered.Configuracao;
        if (configuration.Driver != request.Agent)
            return $"O agente {registered.Descricao} usa {configuration.Driver}, não {request.Agent}.";
        if (!registered.PermitePerfil(request.Profile)) return AboveMaximum(registered, request.Profile);
        // Modelo e esforço são os da versão congelada: o registro aponta para ela, então o processo também.
        var selection = request.ModelSelection ?? AgentModelSelection.CliDefault;
        if (!string.Equals(selection.Model, configuration.Modelo, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(selection.Effort, configuration.Esforco, StringComparison.OrdinalIgnoreCase))
            return $"Modelo e esforço da sessão diferem da versão {registered.Descricao}; nada foi iniciado.";
        if (!MontagemDaTarefa.ReposPermitidos(configuration, request.Context))
            return MontagemDaTarefa.RecusaDeRepositorio(registered.Handle, configuration, request.Context);
        if (configuration.Limites.ExecucoesSimultaneas is { } limit && sessions.Values.Count(other =>
                other.RegisteredAgent?.Handle == registered.Handle && !IsEnded(other.Session.State)) >= limit)
            return $"O agente {registered.Handle} já tem {limit} sessão(ões) simultânea(s), o limite da versão.";
        return null;
    }

    private static string AboveMaximum(AgenteDaSessao registered, AgentPermissionProfile profile) =>
        $"O modo {AgentSessionModes.Name(profile)} passa do máximo do agente {registered.Descricao} " +
        $"({AgentSessionModes.Name(registered.PerfilMaximo)}).";

    // A registered agent receives only the D.A.N.T.E. tool servers its version grants; an unknown server is never
    // granted. Built-ins keep everything, as before.
    private static IReadOnlyList<AgentToolServer>? Granted(SessionStartRequest request,
        IReadOnlyList<AgentToolServer>? servers)
    {
        if (servers is null || request.RegisteredAgent is not { } registered) return servers;
        return servers.Where(server => server.Name switch
        {
            ServidorMcpDoBrain.Nome => registered.Configuracao.PermiteFerramenta(FerramentaDoDante.Brain),
            ServidorMcpDePlanilhas.Nome => registered.Configuracao.PermiteFerramenta(FerramentaDoDante.Planilhas),
            _ => false
        }).ToArray();
    }

    // Multi-repository contract first (AD-58), then the registered agent's role (AD-68); both after the conversation
    // contract the drivers always put first.
    private static string? Instructions(SessionStartRequest request)
    {
        var context = ContratoMultirrepositorio.Para(request.Context);
        var agent = request.RegisteredAgent?.Instrucoes;
        return context is null ? agent : agent is null ? context : context + "\n" + agent;
    }

    private static bool IsEnded(AgentSessionState state) =>
        state is AgentSessionState.Closing or AgentSessionState.Closed or AgentSessionState.Failed;

    private sealed class Entry(AgentSession session, IAgentSessionDriver driver, AgentPermissionProfile profile,
        AgentModelSelection? modelSelection)
    {
        private int driverDisposed;

        public AgentSession Session { get; } = session;
        public IAgentSessionDriver Driver { get; } = driver;
        public AgentPermissionProfile Profile { get; set; } = profile;
        public AgentPermissionProfile? PendingProfile { get; set; }
        public AgentModelSelection? ModelSelection { get; } = modelSelection;
        public string? ReportedModel { get; set; }
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAtUtc { get; set; }
        // Number of the session in the execution ledger; null when not mirrored.
        public long? LedgerNumber { get; init; }
        // Registered agent version frozen at start (AD-68); null for the built-ins.
        public AgenteDaSessao? RegisteredAgent { get; init; }
        // Executor of a reserved session (task step, AD-68): receives its events instead of the sink.
        public IAgentSessionEventSink? Observer { get; init; }
        // Cancelled by a close: the pump never waits on Upstream for a session whose close is waiting for the pump.
        public CancellationTokenSource Closing { get; } = new();
        public Task? Pump { get; set; }
        public AgentTurnOutcome? LastTurnOutcome { get; set; }
        // Turns that ended since the session started or was cleared: without any, there is nothing to compact (#121).
        public int TurnsSinceContextReset { get; set; }
        public CompactionRun? Compaction { get; set; }

        // Serializes the operations that consume or cancel upstream requests (answer, expiration, interrupt,
        // steer by interrupt, close): each changes the session state and reaches the driver as one step.
        public SemaphoreSlim Upstream { get; } = new(1, 1);

        public async Task DisposeDriverAsync()
        {
            if (Interlocked.Exchange(ref driverDisposed, 1) == 1)
            {
                return;
            }

            try
            {
                await Driver.DisposeAsync();
            }
            catch (Exception)
            {
                // Disposal kills the process tree; nothing else can be done for this session.
            }
        }
    }

    private sealed record CompactionRun(CancellationTokenSource Cancel);
}
