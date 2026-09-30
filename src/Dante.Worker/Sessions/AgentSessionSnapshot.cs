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
    AgentTurnOutcome? LastTurnOutcome = null);

public sealed record SessionStartRequest(
    long OwnerUserId,
    AgentKind Agent,
    JobExecutionContext Context,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    AgentPermissionProfile Profile = AgentPermissionProfile.Manual);

// DiscardedMessages: queued messages dropped by an interrupt.
public sealed record SessionResult(
    bool Accepted,
    AgentSessionSnapshot? Session = null,
    string? Error = null,
    int DiscardedMessages = 0)
{
    public static SessionResult Reject(string error) => new(false, Error: error);
}

public sealed record SessionSubmitResult(
    SubmitOutcome Outcome,
    string? SessionId = null,
    string? TurnId = null,
    string? Error = null)
{
    public static SessionSubmitResult Reject(string error, string? sessionId = null) =>
        new(SubmitOutcome.Rejected, sessionId, Error: error);
}
