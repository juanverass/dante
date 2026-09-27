namespace Dante.Worker.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
