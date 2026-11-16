namespace Dante.Worker.Sessions;

// Short ids are typed by users (/approve R000001), so they are global per Worker, like job ids.
public sealed class SessionIdGenerator
{
    private long nextSession;
    private long nextTurn;
    private long nextRequest;

    public string NextSessionId() => NextSessionId(null).Id;

    // With the execution ledger (AD-66) the number comes from the database sequence. A reserved number at or below
    // one already given here (the counter ran while the database was away) falls back to the counter: never a
    // repeated id. Reserved says whether the reserved number was used.
    public (string Id, bool Reserved) NextSessionId(long? reserved)
    {
        while (true)
        {
            var current = Interlocked.Read(ref nextSession);
            var useReserved = reserved > current;
            var next = useReserved ? reserved!.Value : current + 1;
            if (Interlocked.CompareExchange(ref nextSession, next, current) == current)
                return ($"S{next:D6}", useReserved);
        }
    }

    public string NextTurnId() => $"T{Interlocked.Increment(ref nextTurn):D6}";

    public string NextRequestId() => $"R{Interlocked.Increment(ref nextRequest):D6}";
}
