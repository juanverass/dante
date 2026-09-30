namespace Dante.Worker.Sessions;

// The agent broke the structured protocol or its process ended unexpectedly. The driver ends the event stream with
// this exception and kills the process; the caller marks the session Failed.
public sealed class AgentProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);
