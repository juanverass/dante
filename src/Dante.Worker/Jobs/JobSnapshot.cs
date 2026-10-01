using Dante.Worker.Agents;

namespace Dante.Worker.Jobs;

public sealed record JobSnapshot(
    string Id,
    string Agent,
    JobStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    bool CancellationRequested,
    int? ExitCode,
    string? ErrorMessage,
    JobExecutionContext Context,
    AgentModelSelection? ModelSelection = null);
