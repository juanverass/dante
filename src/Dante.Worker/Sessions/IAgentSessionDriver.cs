namespace Dante.Worker.Sessions;

public sealed record AgentSessionStartOptions(
    string WorkingDirectory,
    bool IsGeneral = false,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

// Upstream session id (Claude session_id, Codex thread.id) and the OS id of the process that serves it.
public sealed record AgentSessionStarted(string UpstreamSessionId, int ProcessId);

// What each structured protocol offers, as validated by the #61 spikes (docs/spikes/interactive-protocols).
public sealed record AgentDriverCapabilities(bool NativeSteer, bool Approvals, bool UserInput)
{
    // Claude stream-json: no mid-turn steer; approvals and AskUserQuestion via --permission-prompt-tool stdio.
    public static AgentDriverCapabilities Claude { get; } = new(NativeSteer: false, Approvals: true, UserInput: true);

    // Codex app-server: turn/steer; approvals and item/tool/requestUserInput as server requests. User input is
    // EXPERIMENTAL: it needs capabilities.experimentalApi and the plan collaboration mode on 0.157.1.
    public static AgentDriverCapabilities Codex { get; } = new(NativeSteer: true, Approvals: true, UserInput: true);
}

// One driver instance owns one long-lived agent process for one session. Upstream ids (Claude session_id,
// Codex threadId/turnId, JSON-RPC ids) stay inside the driver, except the request id echoed back to it.
public interface IAgentSessionDriver : IAsyncDisposable
{
    AgentDriverCapabilities Capabilities { get; }

    // Starts the process and the upstream session/thread; one call per driver instance.
    Task<AgentSessionStarted> StartAsync(AgentSessionStartOptions options, CancellationToken cancellationToken = default);

    Task StartTurnAsync(string input, CancellationToken cancellationToken = default);

    // Only when Capabilities.NativeSteer; applied at the next model boundary, not preemptively.
    Task SteerAsync(string input, CancellationToken cancellationToken = default);

    Task InterruptTurnAsync(CancellationToken cancellationToken = default);

    Task RespondAsync(string upstreamRequestId, AgentUserResponse response,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken cancellationToken = default);

    // Closes stdin and waits for the process to exit; no input is accepted afterwards.
    Task CloseAsync(CancellationToken cancellationToken = default);
}
