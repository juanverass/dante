namespace Dante.Worker.Sessions;

public enum AgentApprovalDecision
{
    ApproveOnce,
    ApproveForSession,
    Deny
}

public abstract record AgentUserResponse;

public sealed record AgentApprovalResponse(AgentApprovalDecision Decision, string? Reason = null) : AgentUserResponse;

// Answers keyed by AgentQuestion.Id.
public sealed record AgentInputResponse(IReadOnlyDictionary<string, string> Answers) : AgentUserResponse;
