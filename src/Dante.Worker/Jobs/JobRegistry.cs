using Dante.Worker.Agents;

namespace Dante.Worker.Jobs;

public sealed class JobRegistry
{
    private const int RecentCompletedLimit = 20;
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.OrdinalIgnoreCase);
    private long nextId;

    public (JobSnapshot Snapshot, CancellationToken Token) Create(string agent, JobExecutionContext context,
        CancellationToken stoppingToken)
    {
        var job = new Job($"J{Interlocked.Increment(ref nextId):D6}", agent, context,
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
        lock (gate)
        {
            jobs.Add(job.Id, job);
            return (Snapshot(job), job.Cancellation.Token);
        }
    }

    public bool TryStart(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || job.Status != JobStatus.Queued ||
                job.Cancellation.IsCancellationRequested)
            {
                return false;
            }

            job.Status = JobStatus.Running;
            job.StartedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool TryCancel(string id, out JobSnapshot? snapshot)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || job.Status is not (JobStatus.Queued or JobStatus.Running))
            {
                snapshot = null;
                return false;
            }

            job.CancellationRequested = true;
            job.Cancellation.Cancel();
            snapshot = Snapshot(job);
            return true;
        }
    }

    public JobSnapshot Complete(string id, AgentProcessStatus status, int? exitCode = null,
        string? errorMessage = null)
    {
        lock (gate)
        {
            var job = jobs[id];
            job.Status = job.Cancellation.IsCancellationRequested ? JobStatus.Cancelled : status switch
            {
                AgentProcessStatus.Succeeded => JobStatus.Succeeded,
                AgentProcessStatus.Cancelled => JobStatus.Cancelled,
                _ => JobStatus.Failed
            };
            job.FinishedAtUtc = DateTimeOffset.UtcNow;
            job.ExitCode = exitCode;
            job.ErrorMessage = errorMessage;
            var snapshot = Snapshot(job);
            job.Cancellation.Dispose();
            PruneCompleted();
            return snapshot;
        }
    }

    public IReadOnlyList<JobSnapshot> GetVisible()
    {
        lock (gate)
        {
            return jobs.Values
                .OrderByDescending(job => job.CreatedAtUtc)
                .ThenByDescending(job => job.Id, StringComparer.Ordinal)
                .Select(Snapshot)
                .ToArray();
        }
    }

    private void PruneCompleted()
    {
        var completed = jobs.Values
            .Where(job => job.Status is not (JobStatus.Queued or JobStatus.Running))
            .OrderByDescending(job => job.FinishedAtUtc)
            .ThenByDescending(job => job.Id, StringComparer.Ordinal)
            .Skip(RecentCompletedLimit);
        foreach (var job in completed.ToArray())
        {
            jobs.Remove(job.Id);
        }
    }

    private static JobSnapshot Snapshot(Job job) => new(job.Id, job.Agent, job.Status,
        job.CreatedAtUtc, job.StartedAtUtc, job.FinishedAtUtc,
        job.CancellationRequested, job.ExitCode, job.ErrorMessage, job.Context);

    private sealed class Job(string id, string agent, JobExecutionContext context,
        CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public string Agent { get; } = agent;
        public JobExecutionContext Context { get; } = context;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public JobStatus Status { get; set; } = JobStatus.Queued;
        public bool CancellationRequested { get; set; }
        public int? ExitCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
