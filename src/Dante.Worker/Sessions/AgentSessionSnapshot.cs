using Dante.Worker.Agents;
using Dante.Worker.Jobs;

namespace Dante.Worker.Sessions;

public sealed record AgentSessionSnapshot(
    string Id,
    AgentKind Agent,
    long OwnerUserId,
    JobExecutionContext Context,
    AgentPermissionProfile Profile,
    AgentSessionState State,
    string? ActiveTurnId,
    int QueuedCount,
    IReadOnlyList<string> PendingRequestIds,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? Error,
    AgentTurnOutcome? LastTurnOutcome = null,
    AgentModelSelection? ModelSelection = null,
    string? ReportedModel = null)
{
    // A requested override is not the effective mode until the driver confirms it.
    public AgentPermissionProfile? PendingProfile { get; init; }

    // Claude session_id or Codex thread id now serving the session; a clear replaces it (#120).
    public string? UpstreamSessionId { get; init; }

    // A /compact is running in the background (#121): messages and other context operations wait for its end.
    public bool Compacting { get; init; }

    // The model chosen when the session started, or the CLI default with the model the CLI reported, if any (#77).
    public string EffortLabel => ModelSelection?.EffortLabel ?? "padrão da CLI";

    public string ModelLabel => ModelSelection?.Model ??
        (ReportedModel is null ? AgentModelSelection.CliDefault.ModelLabel : $"padrão da CLI ({ReportedModel})");
}

public sealed record SessionStartRequest(
    long OwnerUserId,
    AgentKind Agent,
    JobExecutionContext Context,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    AgentPermissionProfile Profile = AgentPermissionProfile.Manual,
    AgentModelSelection? ModelSelection = null);

// DiscardedMessages: queued messages dropped by an interrupt.
public sealed record SessionResult(
    bool Accepted,
    AgentSessionSnapshot? Session = null,
    string? Error = null,
    int DiscardedMessages = 0)
{
    public static SessionResult Reject(string error) => new(false, Error: error);
}

// /compact (#121): Started says whether it was accepted; Completion, when accepted, ends with its outcome.
public sealed record SessionCompaction(SessionResult Started, Task<SessionCompactionResult>? Completion = null);

// PreTokens/PostTokens only when the agent reports them (Claude); never estimated.
public sealed record SessionCompactionResult(
    bool Compacted,
    AgentSessionSnapshot? Session = null,
    string? Error = null,
    int? PreTokens = null,
    int? PostTokens = null);

public sealed record SessionSubmitResult(
    SubmitOutcome Outcome,
    string? SessionId = null,
    string? TurnId = null,
    string? Error = null)
{
    public static SessionSubmitResult Reject(string error, string? sessionId = null) =>
        new(SubmitOutcome.Rejected, sessionId, Error: error);
}
