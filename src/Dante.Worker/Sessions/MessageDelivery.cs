namespace Dante.Worker.Sessions;

// Queue is the conservative default; Steer must be chosen explicitly by the user.
public enum MessageDelivery
{
    Queue,
    Steer
}

public enum SubmitOutcome
{
    // A new turn was opened: send the text with IAgentSessionDriver.StartTurnAsync.
    TurnStarted,
    // Kept by the D.A.N.T.E.; starts as a new turn when the active turn ends.
    Queued,
    // Driver steers natively: send the text with IAgentSessionDriver.SteerAsync.
    Steered,
    // No native steer: call IAgentSessionDriver.InterruptTurnAsync; the text runs next.
    SteerByInterrupt,
    Rejected
}

public sealed record SubmitResult(SubmitOutcome Outcome, string? TurnId = null, string? Error = null)
{
    public static SubmitResult Reject(string error) => new(SubmitOutcome.Rejected, Error: error);
}
