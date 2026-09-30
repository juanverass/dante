using Dante.Worker.Jobs;

namespace Dante.Worker.Sessions;

// In-memory catalog of interactive sessions for the life of the Worker (AD-20). It owns one driver per session,
// routes each user operation to the session it names (or to the user's active session) after checking ownership,
// and pumps driver events into the AgentSession state machine. Agent, context and permission profile are fixed when
// the session starts: /use or /agent set never reach a running session. Sessions are not jobs (AD-16) and do not
// survive a Worker restart: stopping the host fails every live session and kills its process.
public sealed class SessionRegistry(
    IAgentSessionDriverFactory drivers,
    ILogger<SessionRegistry> logger,
    IAgentSessionEventSink? sink = null,
    SessionIdGenerator? ids = null,
    TimeSpan? requestTimeout = null) : IAsyncDisposable, IDisposable
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
        Entry entry;
        lock (gate)
        {
            if (disposed)
            {
                return SessionResult.Reject("O Worker está sendo encerrado; não é possível iniciar sessões.");
            }

            var driver = drivers.Create(request.Agent);
            var session = new AgentSession(ids.NextSessionId(), request.Agent, request.OwnerUserId, request.Context,
                driver.Capabilities, ids, requestTimeout);
            entry = new Entry(session, driver, request.Profile);
            sessions.Add(session.Id, entry);
        }

        try
        {
            var started = await entry.Driver.StartAsync(new AgentSessionStartOptions(
                request.Context.WorkingDirectory,
                request.Context.Mode == JobExecutionMode.General,
                request.EnvironmentVariables,
                request.Profile), cancellationToken);
            entry.Session.MarkStarted(started);
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
        lock (gate)
        {
            activeSessions[request.OwnerUserId] = entry.Session.Id;
        }

        // Messages sent to the session id while it was starting are waiting in its queue.
        await StartNextQueuedAsync(entry, cancellationToken);
        return await EndIfFailedAsync(entry);
    }

    // sessionId null routes to the user's active session.
    public async Task<SessionSubmitResult> SubmitAsync(long userId, string? sessionId, string text,
        MessageDelivery delivery = MessageDelivery.Queue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var entry = Find(userId, sessionId, out var error);
        if (entry is null)
        {
            return SessionSubmitResult.Reject(error!, sessionId);
        }

        var session = entry.Session;
        var result = session.Submit(text, delivery);
        try
        {
            switch (result.Outcome)
            {
                case SubmitOutcome.TurnStarted:
                    await entry.Driver.StartTurnAsync(text, cancellationToken);
                    break;
                case SubmitOutcome.Steered:
                    await entry.Driver.SteerAsync(text, cancellationToken);
                    break;
                case SubmitOutcome.SteerByInterrupt:
                    await entry.Driver.InterruptTurnAsync(cancellationToken);
                    break;
            }
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
            await FailAsync(entry, "Não foi possível enviar a mensagem ao agente; a sessão foi encerrada.");
            await entry.DisposeDriverAsync();
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return SessionSubmitResult.Reject(session.Error!, session.Id);
        }

        return new SessionSubmitResult(result.Outcome, session.Id, result.TurnId, result.Error);
    }

    // Stop turn (AD-16): pending requests expire and the queue is discarded; the session goes back to Idle.
    public async Task<SessionResult> InterruptAsync(long userId, string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(userId, sessionId, out var error);
        if (entry is null)
        {
            return SessionResult.Reject(error!);
        }

        if (!entry.Session.TryInterrupt(out var discarded))
        {
            return SessionResult.Reject($"A sessão {entry.Session.Id} não tem turno em andamento para interromper.");
        }

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

    public async Task<SessionResult> CloseAsync(long userId, string? sessionId,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(userId, sessionId, out var error);
        if (entry is null)
        {
            return SessionResult.Reject(error!);
        }

        var session = entry.Session;
        var hadTurn = session.ActiveTurnId is not null;
        if (!session.TryClose())
        {
            return SessionResult.Reject($"A sessão {session.Id} já está sendo encerrada.");
        }

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
                session.MarkClosed();
            }

            MarkEnded(entry);
        }

        return new SessionResult(true, Snapshot(entry));
    }

    // Routes an approval or input answer to the session whose turn raised the request; only the owner may answer.
    public async Task<SessionResult> RespondAsync(long userId, string requestId, AgentUserResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(response);
        Entry? entry;
        lock (gate)
        {
            entry = sessions.Values.FirstOrDefault(candidate =>
                candidate.Session.PendingRequestIds.Contains(requestId, StringComparer.OrdinalIgnoreCase));
        }

        if (entry is null)
        {
            return SessionResult.Reject($"A solicitação {requestId} não está pendente.");
        }

        var resolution = entry.Session.Resolve(requestId, userId, response);
        if (!resolution.Accepted)
        {
            return SessionResult.Reject(resolution.Error!);
        }

        try
        {
            await entry.Driver.RespondAsync(resolution.UpstreamRequestId!, response, cancellationToken);
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

    public Task<SessionResult> RespondAsync(long userId, string sessionId, string turnId,
        string requestId, AgentUserResponse response, CancellationToken cancellationToken = default)
    {
        var pending = GetPendingRequest(userId, requestId);
        if (pending is null || !pending.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase) ||
            !pending.TurnId.Equals(turnId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(SessionResult.Reject("Solicitação não encontrada neste turno da sessão."));
        return RespondAsync(userId, requestId, response, cancellationToken);
    }

    // Explicit selection of the active session; null clears it (the next plain message is not a session turn).
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
                    lock (gate) entry.LastTurnOutcome = completed.Outcome;
                }
                if (stamped is ApprovalRequestedEvent { RequestId: var approvalId })
                    _ = ExpireRequestAsync(entry, approvalId, true);
                else if (stamped is UserInputRequestedEvent { RequestId: var inputId })
                    _ = ExpireRequestAsync(entry, inputId, false);
                await PublishAsync(entry, stamped);
                if (stamped is TurnCompletedEvent)
                {
                    await StartNextQueuedAsync(entry, lifetime.Token);
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
            await Task.Delay(requestTimeout, lifetime.Token);
            var resolution = entry.Session.TryExpire(requestId);
            if (!resolution.Accepted) return;
            AgentUserResponse response = isApproval
                ? new AgentApprovalResponse(AgentApprovalDecision.Deny, "Solicitação expirada.")
                : new AgentInputResponse(new Dictionary<string, string>());
            await entry.Driver.RespondAsync(resolution.UpstreamRequestId!, response, lifetime.Token);
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
        if (!entry.Session.TryStartQueued(out _, out var text))
        {
            return;
        }

        try
        {
            await entry.Driver.StartTurnAsync(text!, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao iniciar turno enfileirado na sessão {SessionId} ({ErrorType}).",
                entry.Session.Id, exception.GetType().Name);
            await FailAsync(entry, "Não foi possível enviar a mensagem ao agente; a sessão foi encerrada.");
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
        entry.Session.MarkFailed(error);
        if (entry.Session.State != AgentSessionState.Failed || !MarkEnded(entry))
        {
            return;
        }

        await PublishAsync(entry, new ErrorEvent(entry.Session.Error!)
        {
            SessionId = entry.Session.Id,
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    private async Task PublishAsync(Entry entry, AgentEvent agentEvent)
    {
        if (sink is null)
        {
            return;
        }

        try
        {
            await sink.PublishAsync(Snapshot(entry), agentEvent, lifetime.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha ao publicar evento {EventType} da sessão {SessionId} ({ErrorType}).",
                agentEvent.GetType().Name, entry.Session.Id, exception.GetType().Name);
        }
    }

    private Entry? Find(long userId, string? sessionId, out string? error)
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

    private bool MarkEnded(Entry entry)
    {
        lock (gate)
        {
            if (entry.EndedAtUtc is not null)
            {
                return false;
            }

            entry.EndedAtUtc = DateTimeOffset.UtcNow;
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
                entry.CreatedAtUtc, entry.EndedAtUtc, session.Error, entry.LastTurnOutcome);
        }
    }

    private static bool IsEnded(AgentSessionState state) =>
        state is AgentSessionState.Closing or AgentSessionState.Closed or AgentSessionState.Failed;

    private sealed class Entry(AgentSession session, IAgentSessionDriver driver, AgentPermissionProfile profile)
    {
        private int driverDisposed;

        public AgentSession Session { get; } = session;
        public IAgentSessionDriver Driver { get; } = driver;
        public AgentPermissionProfile Profile { get; } = profile;
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAtUtc { get; set; }
        public Task? Pump { get; set; }
        public AgentTurnOutcome? LastTurnOutcome { get; set; }

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
}
