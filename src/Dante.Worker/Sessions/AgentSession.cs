using Dante.Worker.Agents;
using Dante.Worker.Jobs;

namespace Dante.Worker.Sessions;

public sealed record RequestResolution(bool Accepted, string? UpstreamRequestId = null, string? Error = null)
{
    public static RequestResolution Reject(string error) => new(false, Error: error);
}

// Neutral state machine of one interactive session. It never talks to a process: the caller applies
// driver events here and performs the driver call that each accepted operation asks for.
// Correlation: one session owns exactly one agent process (ProcessId) through one driver, for its whole
// life. A session is not a job: it is not registered in JobRegistry, and its turns have no job id. A turn
// is identified by session + turn id, and a request by its request id, which belongs to one turn.
// Context reuses JobExecutionContext only to describe the mode and working directory.
public sealed class AgentSession(
    string id,
    AgentKind agent,
    long ownerUserId,
    JobExecutionContext context,
    AgentDriverCapabilities capabilities,
    SessionIdGenerator ids)
{
    private readonly object gate = new();
    private readonly LinkedList<string> queue = new();
    private readonly Dictionary<string, PendingRequest> pending = new(StringComparer.OrdinalIgnoreCase);
    private AgentSessionState state = AgentSessionState.Starting;
    private Turn? activeTurn;

    public string Id { get; } = id;
    public AgentKind Agent { get; } = agent;
    public long OwnerUserId { get; } = ownerUserId;
    public JobExecutionContext Context { get; } = context;
    public AgentDriverCapabilities Capabilities { get; } = capabilities;
    public string? UpstreamSessionId { get; private set; }
    public int? ProcessId { get; private set; }
    public string? Error { get; private set; }

    public AgentSessionState State
    {
        get { lock (gate) { return state; } }
    }

    public string? ActiveTurnId
    {
        get { lock (gate) { return activeTurn?.Id; } }
    }

    public int QueuedCount
    {
        get { lock (gate) { return queue.Count; } }
    }

    public IReadOnlyList<string> PendingRequestIds
    {
        get { lock (gate) { return pending.Keys.ToArray(); } }
    }

    public void MarkStarted(AgentSessionStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentException.ThrowIfNullOrWhiteSpace(started.UpstreamSessionId);
        lock (gate)
        {
            Require(state == AgentSessionState.Starting, "iniciar");
            UpstreamSessionId = started.UpstreamSessionId;
            ProcessId = started.ProcessId;
            state = AgentSessionState.Idle;
        }
    }

    public SubmitResult Submit(string text, MessageDelivery delivery = MessageDelivery.Queue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (gate)
        {
            switch (state)
            {
                case AgentSessionState.Idle when queue.Count > 0:
                    // Queued messages have not started yet: never overtake them (steer still goes first).
                    if (delivery == MessageDelivery.Steer)
                    {
                        queue.AddFirst(text);
                    }
                    else
                    {
                        queue.AddLast(text);
                    }

                    return new SubmitResult(SubmitOutcome.Queued);
                case AgentSessionState.Idle:
                    return new SubmitResult(SubmitOutcome.TurnStarted, OpenTurn());
                case AgentSessionState.Starting when delivery == MessageDelivery.Queue:
                    queue.AddLast(text);
                    return new SubmitResult(SubmitOutcome.Queued);
                case AgentSessionState.Running or AgentSessionState.WaitingForUser
                    when delivery == MessageDelivery.Queue:
                    queue.AddLast(text);
                    return new SubmitResult(SubmitOutcome.Queued, activeTurn!.Id);
                case AgentSessionState.Running when activeTurn!.InterruptRequested:
                    return SubmitResult.Reject("O turno atual já está sendo interrompido.");
                case AgentSessionState.Running when Capabilities.NativeSteer:
                    return new SubmitResult(SubmitOutcome.Steered, activeTurn.Id);
                case AgentSessionState.Running:
                    // Steer without native support: interrupt now and run this text first, keeping the queue.
                    activeTurn.InterruptRequested = true;
                    ExpirePending();
                    queue.AddFirst(text);
                    return new SubmitResult(SubmitOutcome.SteerByInterrupt, activeTurn.Id);
                case AgentSessionState.WaitingForUser:
                    return SubmitResult.Reject(
                        "Há uma aprovação ou resposta pendente; responda-a ou interrompa o turno.");
                default:
                    return SubmitResult.Reject($"A sessão {Id} não aceita mensagens no estado {state}.");
            }
        }
    }

    // Starts the next queued message when the session is idle; the caller sends it with StartTurnAsync.
    public bool TryStartQueued(out string? turnId, out string? text)
    {
        lock (gate)
        {
            if (state != AgentSessionState.Idle || queue.First is not { } first)
            {
                turnId = null;
                text = null;
                return false;
            }

            queue.RemoveFirst();
            text = first.Value;
            turnId = OpenTurn();
            return true;
        }
    }

    public AgentEvent Apply(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        lock (gate)
        {
            if (state is AgentSessionState.Closed or AgentSessionState.Failed)
            {
                throw new InvalidOperationException($"A sessão {Id} está encerrada ({state}).");
            }

            var stamped = agentEvent with
            {
                SessionId = Id,
                TurnId = activeTurn?.Id,
                TimestampUtc = DateTimeOffset.UtcNow
            };
            if (agentEvent is WarningEvent or ErrorEvent)
            {
                return stamped;
            }

            var turn = activeTurn ?? throw new InvalidOperationException(
                $"Evento {agentEvent.GetType().Name} recebido sem turno ativo na sessão {Id}.");
            switch (stamped)
            {
                case ApprovalRequestedEvent approval:
                    return approval with { RequestId = OpenRequest(turn, approval.UpstreamRequestId, isApproval: true) };
                case UserInputRequestedEvent input:
                    return input with { RequestId = OpenRequest(turn, input.UpstreamRequestId, isApproval: false) };
                case TurnCompletedEvent:
                    ExpirePending();
                    activeTurn = null;
                    if (state is AgentSessionState.Running or AgentSessionState.WaitingForUser)
                    {
                        state = AgentSessionState.Idle;
                    }

                    return stamped;
                default:
                    return stamped;
            }
        }
    }

    public RequestResolution Resolve(string requestId, long userId, AgentUserResponse response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(response);
        lock (gate)
        {
            if (userId != OwnerUserId)
            {
                return RequestResolution.Reject("Somente o dono da sessão pode responder a esta solicitação.");
            }

            if (!pending.TryGetValue(requestId, out var request) || request.TurnId != activeTurn?.Id)
            {
                return RequestResolution.Reject($"A solicitação {requestId} não está pendente nesta sessão.");
            }

            if (request.IsApproval != response is AgentApprovalResponse)
            {
                return RequestResolution.Reject(request.IsApproval
                    ? $"A solicitação {requestId} espera aprovar ou negar."
                    : $"A solicitação {requestId} espera uma resposta em texto.");
            }

            pending.Remove(requestId);
            if (pending.Count == 0 && state == AgentSessionState.WaitingForUser)
            {
                state = AgentSessionState.Running;
            }

            return new RequestResolution(true, request.UpstreamRequestId);
        }
    }

    // Stop turn: pending requests expire and queued messages are discarded; returns how many were discarded.
    public bool TryInterrupt(out int discardedMessages)
    {
        lock (gate)
        {
            discardedMessages = 0;
            if (state is not (AgentSessionState.Running or AgentSessionState.WaitingForUser) ||
                activeTurn!.InterruptRequested)
            {
                return false;
            }

            activeTurn.InterruptRequested = true;
            ExpirePending();
            discardedMessages = queue.Count;
            queue.Clear();
            return true;
        }
    }

    // Close session: the caller interrupts any active turn and closes the driver, then calls MarkClosed.
    public bool TryClose()
    {
        lock (gate)
        {
            if (state is AgentSessionState.Closing or AgentSessionState.Closed or AgentSessionState.Failed)
            {
                return false;
            }

            state = AgentSessionState.Closing;
            ExpirePending();
            queue.Clear();
            return true;
        }
    }

    public void MarkClosed()
    {
        lock (gate)
        {
            Require(state == AgentSessionState.Closing, "encerrar");
            activeTurn = null;
            state = AgentSessionState.Closed;
        }
    }

    // The process died or the protocol broke: terminal, nothing else is sent to the driver.
    public void MarkFailed(string error)
    {
        lock (gate)
        {
            if (state is AgentSessionState.Closed or AgentSessionState.Failed)
            {
                return;
            }

            ExpirePending();
            queue.Clear();
            activeTurn = null;
            Error = error;
            state = AgentSessionState.Failed;
        }
    }

    private string OpenTurn()
    {
        activeTurn = new Turn(ids.NextTurnId());
        state = AgentSessionState.Running;
        return activeTurn.Id;
    }

    private string OpenRequest(Turn turn, string upstreamRequestId, bool isApproval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamRequestId);
        var requestId = ids.NextRequestId();
        if (turn.InterruptRequested || state == AgentSessionState.Closing)
        {
            // Nobody will answer: keep it out of the pending set so any late answer is rejected.
            return requestId;
        }

        pending.Add(requestId, new PendingRequest(turn.Id, upstreamRequestId, isApproval));
        state = AgentSessionState.WaitingForUser;
        return requestId;
    }

    private void ExpirePending()
    {
        pending.Clear();
        if (state == AgentSessionState.WaitingForUser)
        {
            state = AgentSessionState.Running;
        }
    }

    private void Require(bool condition, string operation)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Não é possível {operation} a sessão {Id} no estado {state}.");
        }
    }

    private sealed record PendingRequest(string TurnId, string UpstreamRequestId, bool IsApproval);

    private sealed class Turn(string id)
    {
        public string Id { get; } = id;
        public bool InterruptRequested { get; set; }
    }
}
