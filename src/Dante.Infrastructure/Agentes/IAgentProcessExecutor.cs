using Dante.Application.Agentes;
using Dante.Domain.Agentes;

namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public interface IAgentProcessExecutor
{
    bool IsAvailable(AgentKind agent);

    Task<AgentProcessResult> ExecuteAsync(
        AgentProcessRequest request,
        CancellationToken cancellationToken = default);
}
