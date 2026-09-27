namespace Dante.Worker.Agents;

public sealed record AgentProcessResult(
    AgentProcessStatus Status,
    string StandardOutput,
    string StandardError,
    int? ExitCode,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string? ErrorMessage = null);
