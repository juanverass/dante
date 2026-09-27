namespace Dante.Worker.Agents;

public interface IAgentProcessExecutor
{
    bool IsAvailable(AgentKind agent);

    Task<AgentProcessResult> ExecuteAsync(
        AgentProcessRequest request,
        CancellationToken cancellationToken = default);
}
