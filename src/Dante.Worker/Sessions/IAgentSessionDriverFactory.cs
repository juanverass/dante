using Dante.Worker.Agents;
using Dante.Worker.Attachments;

namespace Dante.Worker.Sessions;

// Every session gets a fresh driver: one driver instance owns one agent process (AD-15).
public interface IAgentSessionDriverFactory
{
    IAgentSessionDriver Create(AgentKind agent);
}

// With a media preparer, every driver is wrapped so audio and video reach the agent as transcript and frames (#96).
public sealed class AgentSessionDriverFactory(IInteractiveAgentProcessLauncher launcher, MediaPreparer? media = null)
    : IAgentSessionDriverFactory
{
    public IAgentSessionDriver Create(AgentKind agent)
    {
        IAgentSessionDriver driver = agent switch
        {
            AgentKind.Claude => new ClaudeSessionDriver(launcher),
            AgentKind.Codex => new CodexSessionDriver(launcher),
            _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, "Agente sem driver interativo.")
        };
        return media is null ? driver : new MediaPreparingSessionDriver(driver, media);
    }
}
