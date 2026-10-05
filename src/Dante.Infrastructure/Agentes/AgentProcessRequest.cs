using Dante.Domain.Agentes;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public sealed record AgentProcessRequest(
    AgentKind Agent,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    bool IsGeneral = false,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);
