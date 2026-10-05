using System.Collections.Concurrent;
using System.Threading.Channels;
using Dante.Worker.Attachments;
using Dante.Worker.Sessions;

namespace Dante.Tests;

// In-memory driver: records every call and emits the events the test pushes, with no agent process.
internal sealed class FakeSessionDriver(AgentDriverCapabilities capabilities) : IAgentSessionDriver
{
    private readonly Channel<AgentEvent> events = Channel.CreateUnbounded<AgentEvent>();
    private readonly ConcurrentQueue<string> calls = new();
    private readonly ConcurrentQueue<AgentInput> turnInputs = new();
    private readonly ConcurrentQueue<(string RequestId, AgentUserResponse Response)> responses = new();
    private volatile bool interrupted;

    public AgentDriverCapabilities Capabilities { get; set; } = capabilities;
    public AgentSessionStartOptions? StartOptions { get; private set; }
    public Exception? StartFailure { get; set; }
    public Exception? ModeFailure { get; set; }
    public TaskCompletionSource? ModeGate { get; set; }
    private AgentPermissionProfile? pendingMode;
    public Exception? TurnFailure { get; set; }
    // /clear (#120): failure to throw, or a gate that holds the operation until the test completes it.
    public Exception? ClearFailure { get; set; }
    public TaskCompletionSource? ClearGate { get; set; }
    // /compact (#121): like the real drivers, cancelling while held by the gate ends as "unchanged".
    public Exception? CompactFailure { get; set; }
    public TaskCompletionSource? CompactGate { get; set; }
    public AgentContextCompacted Compacted { get; set; } = new(5201, 592);
    private int clears;
    public Exception? SteerFailure { get; set; }
    public Exception? InterruptFailure { get; set; }
    public Exception? ResponseFailure { get; set; }
    // Holds RespondAsync (after recording the call) until the test completes it.
    public TaskCompletionSource? ResponseGate { get; set; }
    // Like the real drivers: interrupting or closing clears the upstream requests, so a later answer is refused.
    public bool RejectResponsesAfterInterrupt { get; set; }
    public bool Disposed { get; private set; }
    public IReadOnlyList<string> Calls => calls.ToArray();
    public IReadOnlyList<AgentInput> TurnInputs => turnInputs.ToArray();
    public IReadOnlyList<(string RequestId, AgentUserResponse Response)> Responses => responses.ToArray();

    // Text-only inputs are recorded as before; attachments are listed after the text by id.
    private static string Describe(AgentInput input) => input.Attachments.Count == 0 ? input.Text
        : input.Text + " [" + string.Join(",", input.Attachments.Select(attachment => attachment.Id)) + "]";

    public void Emit(AgentEvent agentEvent) => events.Writer.TryWrite(agentEvent);

    public void Crash(Exception exception) => events.Writer.TryComplete(exception);

    public Task<AgentSessionStarted> StartAsync(AgentSessionStartOptions options,
        CancellationToken cancellationToken = default)
    {
        StartOptions = options;
        calls.Enqueue("start");
        return StartFailure is null
            ? Task.FromResult(new AgentSessionStarted("upstream-1", 4242))
            : Task.FromException<AgentSessionStarted>(StartFailure);
    }

    public async Task ChangeModeAsync(AgentPermissionProfile profile, CancellationToken cancellationToken = default)
    {
        calls.Enqueue($"mode:{AgentSessionModes.Name(profile)}");
        if (ModeGate is not null) await ModeGate.Task.WaitAsync(cancellationToken);
        if (ModeFailure is not null) throw ModeFailure;
        if (Capabilities.ModeSwitch == AgentModeSwitch.NextTurn) pendingMode = profile;
    }

    public async Task<AgentContextCleared> ClearContextAsync(CancellationToken cancellationToken = default)
    {
        calls.Enqueue("clear");
        if (ClearGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        if (ClearFailure is not null) throw ClearFailure;
        return new AgentContextCleared($"upstream-cleared-{Interlocked.Increment(ref clears)}");
    }

    public async Task<AgentContextCompacted> CompactContextAsync(CancellationToken cancellationToken = default)
    {
        calls.Enqueue("compact");
        if (CompactGate is { } gate)
        {
            try { await gate.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                calls.Enqueue("compact-interrupt");
                throw new AgentContextUnchangedException("A compactação foi cancelada; a conversa anterior foi mantida.");
            }
        }
        if (CompactFailure is not null) throw CompactFailure;
        return Compacted;
    }

    public Task StartTurnAsync(AgentInput input, CancellationToken cancellationToken = default)
    {
        if (TurnFailure is not null)
        {
            pendingMode = null;
            return Task.FromException(TurnFailure);
        }
        if (pendingMode is { } applied)
        {
            Emit(new ModeAppliedEvent(applied));
            pendingMode = null;
        }
        turnInputs.Enqueue(input);
        calls.Enqueue("turn:" + Describe(input));
        return TurnFailure is null ? Task.CompletedTask : Task.FromException(TurnFailure);
    }

    public Task SteerAsync(AgentInput input, CancellationToken cancellationToken = default)
    {
        calls.Enqueue("steer:" + Describe(input));
        return SteerFailure is null ? Task.CompletedTask : Task.FromException(SteerFailure);
    }

    public Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        calls.Enqueue("interrupt");
        interrupted = true;
        return InterruptFailure is null ? Task.CompletedTask : Task.FromException(InterruptFailure);
    }

    public async Task RespondAsync(string upstreamRequestId, AgentUserResponse response,
        CancellationToken cancellationToken = default)
    {
        calls.Enqueue("respond:" + upstreamRequestId);
        if (ResponseGate is { } gate)
        {
            await gate.Task;
        }

        if (RejectResponsesAfterInterrupt && interrupted)
        {
            throw new InvalidOperationException($"A solicitação {upstreamRequestId} não está pendente.");
        }

        responses.Enqueue((upstreamRequestId, response));
        if (ResponseFailure is not null)
        {
            throw ResponseFailure;
        }
    }

    public IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
        events.Reader.ReadAllAsync(cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        calls.Enqueue("close");
        events.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSessionDriverFactory : IAgentSessionDriverFactory
{
    private readonly ConcurrentQueue<FakeSessionDriver> created = new();

    public Action<FakeSessionDriver>? Configure { get; set; }
    // Like the production factory with a preparer: audio and video go through MediaPreparingSessionDriver (#96).
    public MediaPreparer? Media { get; set; }
    public IReadOnlyList<FakeSessionDriver> Created => created.ToArray();

    public IAgentSessionDriver Create(AgentKind agent)
    {
        var driver = new FakeSessionDriver(agent == AgentKind.Codex
            ? AgentDriverCapabilities.Codex
            : AgentDriverCapabilities.Claude);
        Configure?.Invoke(driver);
        created.Enqueue(driver);
        return Media is null ? driver : new MediaPreparingSessionDriver(driver, Media);
    }
}

internal sealed class RecordingSessionSink : IAgentSessionEventSink
{
    private readonly ConcurrentQueue<(AgentSessionSnapshot Session, AgentEvent Event)> published = new();

    public IReadOnlyList<(AgentSessionSnapshot Session, AgentEvent Event)> Published => published.ToArray();

    public Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        published.Enqueue((session, agentEvent));
        return Task.CompletedTask;
    }
}
