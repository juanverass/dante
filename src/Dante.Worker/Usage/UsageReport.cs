using Dante.Worker.Agents;

namespace Dante.Worker.Usage;

// A subscription quota window as the provider reports it (AD-31): share of the limit already used (0–100), when it
// resets and how long it lasts. Label names a window that is neither the session nor the week (another bucket/model).
public sealed record QuotaWindow(decimal UsedPercent, DateTimeOffset? ResetsAt, TimeSpan? Duration, string? Label = null);

// Either a window or the reason it is unavailable: a metric the provider did not give is never zero nor estimated.
public sealed record QuotaMetric(QuotaWindow? Window, string? UnavailableReason)
{
    public static QuotaMetric Of(QuotaWindow window) => new(window, null);

    public static QuotaMetric Unavailable(string reason) => new(null, reason);
}

// The quotas of the account authenticated in the agent's CLI, which include usage outside the D.A.N.T.E. QueriedAt is
// when the answer arrived; Additional windows are shown apart and never added to the session or the week.
public sealed record UsageReport(
    AgentKind Agent,
    DateTimeOffset QueriedAt,
    QuotaMetric Session,
    QuotaMetric Weekly,
    IReadOnlyList<QuotaWindow> Additional,
    string? Note = null);

public enum UsageQueryFailure
{
    NotAuthenticated,
    NoSubscription,
    Unsupported,
    Timeout,
    Failed
}

// A query that produced no metric at all. The message is shown to the user and never carries account data.
public sealed class UsageQueryException(UsageQueryFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UsageQueryFailure Failure { get; } = failure;
}

public interface IUsageQuotaReader
{
    // Reads the agent's quotas now; throws UsageQueryException when the CLI cannot tell them.
    Task<UsageReport> ReadAsync(AgentKind agent, CancellationToken cancellationToken = default);
}
