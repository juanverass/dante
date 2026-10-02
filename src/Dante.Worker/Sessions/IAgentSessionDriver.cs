using Dante.Worker.Agents;

namespace Dante.Worker.Sessions;

public sealed record AgentSessionStartOptions(
    string WorkingDirectory,
    bool IsGeneral = false,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    AgentPermissionProfile Profile = AgentPermissionProfile.Manual,
    AgentModelSelection? ModelSelection = null);

// Upstream session id (Claude session_id, Codex thread.id) and the OS id of the process that serves it. Model is the
// model the CLI reports for the session when it says so at start (Codex thread/start), even without a selection.
public sealed record AgentSessionStarted(string UpstreamSessionId, int ProcessId, string? Model = null);

// An explicit refusal before application is different from an uncertain upstream mode.
public sealed class AgentModeRejectedException(string message) : Exception(message);

public sealed class AgentModeUnconfirmedException(string message) : Exception(message);

public enum AgentModeSwitch { Unsupported, Idle, NextTurn }

// Validated protocol capabilities (#61 and #108; docs/spikes/).
public sealed record AgentDriverCapabilities(bool NativeSteer, bool Approvals, bool UserInput)
{
    // Modes (permission profiles) the driver maps to its CLI; a session in any other mode is refused before it starts.
    public IReadOnlyList<AgentPermissionProfile> Modes { get; init; } = AgentSessionModes.All;

    public AgentModeSwitch ModeSwitch { get; init; } = AgentModeSwitch.Unsupported;

    // Images reach the model in the turn and in a native steer: Claude as base64 image blocks, Codex as localImage
    // items (#93 spike, AD-29). Attachments are refused before reaching a driver without it.
    public bool ImageInput { get; init; } = true;

    // Audio and video are prepared by the D.A.N.T.E. before the turn reaches the agent (#96): transcript and frames
    // (MediaPreparingSessionDriver). No CLI receives them natively, so only that wrapper declares it.
    public bool MediaInput { get; init; }

    // Claude stream-json: no mid-turn steer; approvals and AskUserQuestion via --permission-prompt-tool stdio.
    // Modes map to --permission-mode manual|auto|plan (AD-18).
    public static AgentDriverCapabilities Claude { get; } = new(NativeSteer: false, Approvals: true, UserInput: true) { ModeSwitch = AgentModeSwitch.Idle };

    // Codex app-server: turn/steer; approvals and item/tool/requestUserInput as server requests. User input is
    // EXPERIMENTAL: it needs capabilities.experimentalApi and the plan collaboration mode on 0.157.1.
    // Modes map to approvalPolicy/sandbox and the plan collaboration mode (AD-19).
    public static AgentDriverCapabilities Codex { get; } = new(NativeSteer: true, Approvals: true, UserInput: true) { ModeSwitch = AgentModeSwitch.NextTurn };

    public static AgentDriverCapabilities For(AgentKind agent) => agent == AgentKind.Codex ? Codex : Claude;
}

// One driver instance owns one long-lived agent process for one session. Upstream ids (Claude session_id,
// Codex threadId/turnId, JSON-RPC ids) stay inside the driver, except the request id echoed back to it.
public interface IAgentSessionDriver : IAsyncDisposable
{
    AgentDriverCapabilities Capabilities { get; }

    // Starts the process and the upstream session/thread; one call per driver instance.
    Task<AgentSessionStarted> StartAsync(AgentSessionStartOptions options, CancellationToken cancellationToken = default);

    // Idle only. NextTurn drivers schedule an override; ModeAppliedEvent confirms the effective policy later.
    Task ChangeModeAsync(AgentPermissionProfile profile, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Este agente não suporta troca de modo nesta sessão."));

    Task StartTurnAsync(AgentInput input, CancellationToken cancellationToken = default);

    // Only when Capabilities.NativeSteer; applied at the next model boundary, not preemptively.
    Task SteerAsync(AgentInput input, CancellationToken cancellationToken = default);

    Task InterruptTurnAsync(CancellationToken cancellationToken = default);

    Task RespondAsync(string upstreamRequestId, AgentUserResponse response,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentEvent> ReadEventsAsync(CancellationToken cancellationToken = default);

    // Closes stdin and waits for the process to exit; no input is accepted afterwards.
    Task CloseAsync(CancellationToken cancellationToken = default);
}
