using Dante.Worker.Agents;
using Dante.Worker.Jobs;

namespace Dante.Tests;

public sealed class JobRegistryTests
{
    [Fact]
    public void RetainsActiveJobsAndOnlyTwentyRecentCompletedJobs()
    {
        var jobs = new JobRegistry();
        var active = jobs.Create("Codex", CancellationToken.None);
        Assert.True(jobs.TryStart(active.Snapshot.Id));

        for (var index = 0; index < 25; index++)
        {
            var created = jobs.Create("Claude", CancellationToken.None);
            Assert.True(jobs.TryStart(created.Snapshot.Id));
            jobs.Complete(created.Snapshot.Id, AgentProcessStatus.Failed, 1, "agent failed");
        }

        var visible = jobs.GetVisible();
        Assert.Equal(21, visible.Count);
        Assert.Contains(visible, job => job.Id == active.Snapshot.Id && job.Status == JobStatus.Running);
        Assert.DoesNotContain(visible, job => job.Id == "J000002");
        Assert.Contains(visible, job => job.Id == "J000026" && job.Status == JobStatus.Failed &&
            job.ExitCode == 1 && job.ErrorMessage == "agent failed" &&
            job.StartedAtUtc is not null && job.FinishedAtUtc is not null);

        Assert.True(jobs.TryCancel(active.Snapshot.Id, out _));
        var cancelled = jobs.Complete(active.Snapshot.Id, AgentProcessStatus.Succeeded);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
    }
}
