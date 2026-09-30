using System.Collections.Concurrent;
using System.Threading.Channels;
using Dante.Worker.Agents;
using Dante.Worker.Sessions;

namespace Dante.Tests;

// In-memory driver: records every call and emits the events the test pushes, with no agent process.
internal sealed class FakeSessionDriver(AgentDriverCapabilities capabilities) : IAgentSessionDriver
{
    private readonly Channel<AgentEvent> events = Channel.CreateUnbounded<AgentEvent>();
    private readonly ConcurrentQueue<string> calls = new();
    private readonly ConcurrentQueue<(string RequestId, AgentUserResponse Response)> responses = new();
    private volatile bool interrupted;

    public AgentDriverCapabilities Capabilities { get; } = capabilities;
    public AgentSessionStartOptions? StartOptions { get; private set; }
    public Exception? StartFailure { get; set; }
    public Exception? TurnFailure { get; set; }
    public Exception? SteerFailure { get; set; }
    public Exception? InterruptFailure { get; set; }
    public Exception? ResponseFailure { get; set; }
    // Holds RespondAsync (after recording the call) until the test completes it.
    public TaskCompletionSource? ResponseGate { get; set; }
    // Like the real drivers: interrupting or closing clears the upstream requests, so a later answer is refused.
    public bool RejectResponsesAfterInterrupt { get; set; }
    public bool Disposed { get; private set; }
    public IReadOnlyList<string> Calls => calls.ToArray();
    public IReadOnlyList<(string RequestId, AgentUserResponse Response)> Responses => responses.ToArray();

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

    public Task StartTurnAsync(string input, CancellationToken cancellationToken = default)
    {
        calls.Enqueue("turn:" + input);
        return TurnFailure is null ? Task.CompletedTask : Task.FromException(TurnFailure);
    }

    public Task SteerAsync(string input, CancellationToken cancellationToken = default)
    {
        calls.Enqueue("steer:" + input);
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
    public IReadOnlyList<FakeSessionDriver> Created => created.ToArray();

    public IAgentSessionDriver Create(AgentKind agent)
    {
        var driver = new FakeSessionDriver(agent == AgentKind.Codex
            ? AgentDriverCapabilities.Codex
            : AgentDriverCapabilities.Claude);
        Configure?.Invoke(driver);
        created.Enqueue(driver);
        return driver;
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
