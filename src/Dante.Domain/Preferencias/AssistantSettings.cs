using Dante.Domain.Agentes;

namespace Dante.Domain.Preferencias;

// Legado movido do Worker na #166: o nome em inglês fica até a migração explícita (AD-38).
public sealed record AssistantSettings(AgentKind DefaultAgent)
{
    public static AssistantSettings Default { get; } = new(AgentKind.Claude);
}
