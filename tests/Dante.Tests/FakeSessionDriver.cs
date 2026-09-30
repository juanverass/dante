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

    public AgentDriverCapabilities Capabilities { get; } = capabilities;
    public AgentSessionStartOptions? StartOptions { get; private set; }
    public Exception? StartFailure { get; set; }
    public Exception? TurnFailure { get; set; }
    public Exception? SteerFailure { get; set; }
    public Exception? InterruptFailure { get; set; }
    public Exception? ResponseFailure { get; set; }
    public bool Disposed { get; private set; }
    public IReadOnlyList<string> Calls => calls.ToArray();

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
        return InterruptFailure is null ? Task.CompletedTask : Task.FromException(InterruptFailure);
    }

    public Task RespondAsync(string upstreamRequestId, AgentUserResponse response,
        CancellationToken cancellationToken = default)
    {
        calls.Enqueue("respond:" + upstreamRequestId);
        return ResponseFailure is null ? Task.CompletedTask : Task.FromException(ResponseFailure);
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
