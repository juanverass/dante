using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;


namespace Dante.Worker.Jobs;

// Runtime of one-shot jobs (AD-06). With the execution ledger enabled (AD-66), each job is also mirrored as an
// Avulsa execution: the J… number comes from the database sequence and never repeats after a restart. The registry
// stays the source of the live state; the mirror never blocks or fails a job. Each job records the user who asked
// for it: listing and cancelling on behalf of a user only reach that user's jobs (#305); a job of someone else
// answers exactly as one that does not exist.
public sealed class JobRegistry(EspelhoDoLedger? ledger = null)
{
    private const int RecentCompletedLimit = 20;
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.OrdinalIgnoreCase);
    private long lastNumber;

    // The model is fixed when the job is created (#77); null keeps the CLI default. Never mirrored: local counter.
    public (JobSnapshot Snapshot, CancellationToken Token) Create(string agent, JobExecutionContext context,
        CancellationToken stoppingToken, AgentModelSelection? modelSelection = null, long? ownerId = null) =>
        Add(agent, context, stoppingToken, modelSelection, null, null, ownerId);

    // Same as Create, for the user who asked; with the ledger, reserves the number in the database first. A database
    // that does not answer keeps the legacy behavior: local counter, no mirror.
    public async Task<(JobSnapshot Snapshot, CancellationToken Token)> CreateAsync(string agent,
        JobExecutionContext context, long userId, CancellationToken stoppingToken,
        AgentModelSelection? modelSelection = null)
    {
        long used;
        lock (gate) used = lastNumber;
        var reserved = ledger is null ? null : await ledger.ReservarNumeroDeExecucaoAsync(used, stoppingToken);
        return Add(agent, context, stoppingToken, modelSelection, reserved, userId, userId);
    }

    // Highest J… number used by this process (local counter or reservation).
    public long LastNumber
    {
        get { lock (gate) return lastNumber; }
    }

    // Durable queue (AD-67): the number was reserved in the database and the job is recorded by the queue, in the same
    // transaction as its work item, so it is never mirrored here. Null when the number is not above the ones already
    // used (a stale reservation would repeat an id).
    public (JobSnapshot Snapshot, CancellationToken Token)? CreateQueued(long number, string agent,
        JobExecutionContext context, CancellationToken stoppingToken, AgentModelSelection? modelSelection = null,
        long? ownerId = null)
    {
        lock (gate)
        {
            if (number <= lastNumber) return null;
            lastNumber = number;
            var job = new Job(number, $"J{number:D6}", agent, context,
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken), modelSelection) { OwnerId = ownerId };
            jobs.Add(job.Id, job);
            return (Snapshot(job), job.Cancellation.Token);
        }
    }

    private (JobSnapshot Snapshot, CancellationToken Token) Add(string agent, JobExecutionContext context,
        CancellationToken stoppingToken, AgentModelSelection? modelSelection, long? reserved, long? userId, long? ownerId)
    {
        lock (gate)
        {
            // The reservation is above the number used when it was asked; a concurrent create may have used more
            // since. A number at or below one already used here would repeat an id: the job falls back to the
            // counter and is not mirrored, and the next reservation moves the sequence past it.
            var mirrored = ledger is not null && reserved > lastNumber && userId is not null &&
                           Enum.TryParse<AgentKind>(agent, true, out _);
            var number = mirrored ? reserved!.Value : lastNumber + 1;
            lastNumber = number;
            var job = new Job(number, $"J{number:D6}", agent, context,
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken), modelSelection)
            {
                Mirrored = mirrored,
                OwnerId = ownerId
            };
            jobs.Add(job.Id, job);
            if (mirrored)
            {
                var registered = new JobRegistradoDto(number, Enum.Parse<AgentKind>(agent, true),
                    LedgerChannel.Telegram, userId!.Value, context, modelSelection?.Model, modelSelection?.Effort,
                    job.CreatedAtUtc);
                ledger!.Registrar($"criação do job {job.Id}", (l, token) => l.RegistrarJobCriadoAsync(registered, token));
            }
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
            if (job.Mirrored)
            {
                var (number, started) = (job.Number, job.StartedAtUtc.Value);
                ledger!.Registrar($"início do job {job.Id}", (l, token) => l.RegistrarJobIniciadoAsync(number, started, token));
            }
            return true;
        }
    }

    // Cancellation by the system (lost queue ownership, shutdown paths): no owner check.
    public bool TryCancel(string id, out JobSnapshot? snapshot) => TryCancel(id, null, out snapshot);

    // Cancellation asked by a user: a job of someone else (or without a recorded owner) is "not found".
    public bool TryCancel(string id, long ownerId, out JobSnapshot? snapshot) => TryCancel(id, (long?)ownerId, out snapshot);

    private bool TryCancel(string id, long? ownerId, out JobSnapshot? snapshot)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || (ownerId is { } owner && job.OwnerId != owner) ||
                job.Status is not (JobStatus.Queued or JobStatus.Running))
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
            if (job.Mirrored)
            {
                var ended = new JobEncerradoDto(job.Number, job.Status switch
                {
                    JobStatus.Succeeded => EstadoDaExecucao.Concluida,
                    JobStatus.Cancelled => EstadoDaExecucao.Cancelada,
                    _ => EstadoDaExecucao.Falhou
                }, exitCode, errorMessage, job.FinishedAtUtc.Value);
                ledger!.Registrar($"fim do job {job.Id}", (l, token) => l.RegistrarJobEncerradoAsync(ended, token));
            }
            var snapshot = Snapshot(job);
            job.Cancellation.Dispose();
            PruneCompleted();
            return snapshot;
        }
    }

    public IReadOnlyList<JobSnapshot> GetVisible() => Visible(null);

    // Only the jobs the user asked for (#305).
    public IReadOnlyList<JobSnapshot> GetVisible(long ownerId) => Visible(ownerId);

    private IReadOnlyList<JobSnapshot> Visible(long? ownerId)
    {
        lock (gate)
        {
            return jobs.Values
                .Where(job => ownerId is not { } owner || job.OwnerId == owner)
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
        job.CancellationRequested, job.ExitCode, job.ErrorMessage, job.Context, job.ModelSelection, job.OwnerId);

    private sealed class Job(long number, string id, string agent, JobExecutionContext context,
        CancellationTokenSource cancellation, AgentModelSelection? modelSelection)
    {
        public long Number { get; } = number;
        public string Id { get; } = id;
        public bool Mirrored { get; init; }
        public long? OwnerId { get; init; }
        public string Agent { get; } = agent;
        public JobExecutionContext Context { get; } = context;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public AgentModelSelection? ModelSelection { get; } = modelSelection;
        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public JobStatus Status { get; set; } = JobStatus.Queued;
        public bool CancellationRequested { get; set; }
        public int? ExitCode { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
