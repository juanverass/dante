using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
// A model the installed CLI offers. ResolvedId is the full id behind an alias (Claude "opus" → "claude-opus-5-5").
// EffortLevels are the reasoning effort levels the CLI advertises for the model, in its own names.
public sealed record AgentModelInfo(
    string Id,
    string DisplayName,
    bool IsDefault,
    IReadOnlyList<string> EffortLevels,
    string? ResolvedId = null);

public sealed class AgentModelCatalogException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public interface IAgentModelCatalog
{
    // Models the installed CLI offers now; throws AgentModelCatalogException when the CLI cannot be queried.
    Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(AgentKind agent, CancellationToken cancellationToken = default);
}
