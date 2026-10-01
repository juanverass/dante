namespace Dante.Worker.Sessions;

// Drivers emit events without D.A.N.T.E. ids; AgentSession.Apply stamps session, turn and request ids.
public abstract record AgentEvent
{
    public string SessionId { get; init; } = "";
    public string? TurnId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
}

public sealed record TurnStartedEvent : AgentEvent;

public sealed record MessageDeltaEvent(string ItemId, string Text) : AgentEvent;

public sealed record MessageCompletedEvent(string ItemId, string Text) : AgentEvent;

public enum AgentToolKind
{
    Command,
    FileChange,
    Tool
}

public sealed record ToolStartedEvent(string ItemId, AgentToolKind Kind, string Description) : AgentEvent;

public sealed record ToolCompletedEvent(string ItemId, AgentToolKind Kind, bool Succeeded, string? Output = null)
    : AgentEvent;

public sealed record FileChangeEvent(IReadOnlyList<string> Paths, string? Diff = null) : AgentEvent;

// Warnings and errors may also be session-level (no active turn), e.g. configuration warnings.
public sealed record WarningEvent(string Message) : AgentEvent;

public sealed record ErrorEvent(string Message) : AgentEvent;

public sealed record RequestResolvedEvent(string RequestId, AgentApprovalDecision Decision) : AgentEvent;

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

public sealed record TurnCompletedEvent(AgentTurnOutcome Outcome, string? Error = null) : AgentEvent;
