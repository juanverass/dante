using Dante.Worker.Agents;

namespace Dante.Worker.Settings;

public sealed record AssistantSettings(AgentKind DefaultAgent)
{
    public static AssistantSettings Default { get; } = new(AgentKind.Claude);
}
