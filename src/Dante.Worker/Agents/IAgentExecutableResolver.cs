namespace Dante.Worker.Agents;

public interface IAgentExecutableResolver
{
    string? Resolve(AgentKind agent);
}
