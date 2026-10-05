using Dante.Domain.Agentes;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public interface IAgentExecutableResolver
{
    string? Resolve(AgentKind agent);
}
