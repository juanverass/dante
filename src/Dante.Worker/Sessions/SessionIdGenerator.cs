namespace Dante.Worker.Sessions;

// Short ids are typed by users (/approve R000001), so they are global per Worker, like job ids.
public sealed class SessionIdGenerator
{
    private long nextSession;
    private long nextTurn;
    private long nextRequest;

    public string NextSessionId() => $"S{Interlocked.Increment(ref nextSession):D6}";

    public string NextTurnId() => $"T{Interlocked.Increment(ref nextTurn):D6}";

    public string NextRequestId() => $"R{Interlocked.Increment(ref nextRequest):D6}";
}
