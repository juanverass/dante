namespace Dante.Infrastructure.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public interface IInteractiveAgentProcessLauncher
{
    // Throws AgentProcessStartException when the process cannot start. redactOutput is applied to every
    // stdout/stderr line before it leaves the process wrapper.
    Task<InteractiveAgentProcess> StartAsync(
        AgentProcessRequest request,
        Func<string, string>? redactOutput = null,
        CancellationToken cancellationToken = default);
}
