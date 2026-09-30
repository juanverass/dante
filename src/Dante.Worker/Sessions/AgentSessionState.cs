namespace Dante.Worker.Sessions;

// Starting → Idle ⇄ Running ⇄ WaitingForUser; any live state → Closing → Closed; any live state → Failed.
public enum AgentSessionState
{
    Starting,
    Idle,
    Running,
    WaitingForUser,
    Closing,
    Closed,
    Failed
}

public enum AgentTurnOutcome
{
    Completed,
    Interrupted,
    Failed
}
