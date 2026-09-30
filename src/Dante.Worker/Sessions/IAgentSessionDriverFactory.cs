using Dante.Worker.Agents;

namespace Dante.Worker.Sessions;

// Every session gets a fresh driver: one driver instance owns one agent process (AD-15).
public interface IAgentSessionDriverFactory
{
    IAgentSessionDriver Create(AgentKind agent);
}

public sealed class AgentSessionDriverFactory(IInteractiveAgentProcessLauncher launcher) : IAgentSessionDriverFactory
{
    public IAgentSessionDriver Create(AgentKind agent) => agent switch
    {
        AgentKind.Claude => new ClaudeSessionDriver(launcher),
        AgentKind.Codex => new CodexSessionDriver(launcher),
        _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, "Agente sem driver interativo.")
    };
}
