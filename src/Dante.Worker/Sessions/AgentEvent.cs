namespace Dante.Worker.Sessions;

// Drivers emit events without D.A.N.T.E. ids; AgentSession.Apply stamps session, turn and request ids.
public abstract record AgentEvent
{
    public string SessionId { get; init; } = "";
    public string? TurnId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
}

public sealed record ModeAppliedEvent(AgentPermissionProfile Profile) : AgentEvent;

public sealed record TurnStartedEvent : AgentEvent;

public sealed record MessageDeltaEvent(string ItemId, string Text) : AgentEvent;

public sealed record MessageCompletedEvent(string ItemId, string Text) : AgentEvent;

public enum AgentToolKind
{
    Command,
    FileChange,
    Tool
}

public sealed record ToolStartedEvent(string ItemId, AgentToolKind Kind, string Description) : AgentEvent
{
    public string? Server { get; init; }
    public string? ToolName { get; init; }
    public string? Presentation { get; init; }
}

public sealed record ToolCompletedEvent(string ItemId, AgentToolKind Kind, bool Succeeded, string? Output = null)
    : AgentEvent;

public sealed record FileChangeEvent(IReadOnlyList<string> Paths, string? Diff = null) : AgentEvent;

// A file the CLI reports through a structured channel (Codex imageGeneration.savedPath), never a path read in prose
// (AD-29). Delivery still validates the path against the root of that channel.
public sealed record ArtifactProducedEvent(string ItemId, string Path) : AgentEvent;

// Warnings and errors may also be session-level (no active turn), e.g. configuration warnings.
public sealed record WarningEvent(string Message) : AgentEvent;

public sealed record ErrorEvent(string Message) : AgentEvent;

public sealed record RequestResolvedEvent(string RequestId, AgentApprovalDecision Decision) : AgentEvent;

public sealed record InputResolvedEvent(string RequestId) : AgentEvent;

public sealed record RequestClosedEvent(string RequestId) : AgentEvent;

public sealed record RequestExpiredEvent(string RequestId) : AgentEvent;

public sealed record ApprovalRequestedEvent(
    string UpstreamRequestId,
    AgentToolKind Kind,
    string Action,
    string? Reason = null) : AgentEvent
{
    public string RequestId { get; init; } = "";
    public bool CanApproveForSession { get; init; }
}

public sealed record AgentQuestion(string Id, string Text, IReadOnlyList<string> Options);

public sealed record UserInputRequestedEvent(string UpstreamRequestId, IReadOnlyList<AgentQuestion> Questions)
    : AgentEvent
{
    public string RequestId { get; init; } = "";
}

// Token counts the CLI itself reported for the turn (#148); null when it reported nothing. Input includes cached input.
public sealed record AgentTokenUsage(long? InputTokens, long? OutputTokens, long? CachedInputTokens = null);

public sealed record TurnCompletedEvent(AgentTurnOutcome Outcome, string? Error = null, AgentTokenUsage? Usage = null)
    : AgentEvent
{
    // Correlation of the input that opened the turn, stamped by the session (#148).
    public string? Correlation { get; init; }
}
