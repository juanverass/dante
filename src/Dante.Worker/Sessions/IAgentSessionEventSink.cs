namespace Dante.Worker.Sessions;

// Receives every event already stamped by AgentSession, in order per session. Delivery to the user (Telegram
// streaming, throttling, redaction) is #66; without a sink the registry still applies events to the session state.
public interface IAgentSessionEventSink
{
    Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken);
}
