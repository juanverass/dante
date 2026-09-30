namespace Dante.Worker.Agents;

public interface IInteractiveAgentProcessLauncher
{
    // Throws AgentProcessStartException when the process cannot start. redactOutput is applied to every
    // stdout/stderr line before it leaves the process wrapper.
    Task<InteractiveAgentProcess> StartAsync(
        AgentProcessRequest request,
        Func<string, string>? redactOutput = null,
        CancellationToken cancellationToken = default);
}
