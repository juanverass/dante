namespace Dante.Application.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public sealed record AgentProcessResult(
    AgentProcessStatus Status,
    string StandardOutput,
    string StandardError,
    int? ExitCode,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string? ErrorMessage = null);
