using Dante.Application.Agentes;
using Dante.Worker.Jobs;

namespace Dante.Tests;

public sealed class JobRegistryTests
{
    [Fact]
    public void RetainsActiveJobsAndOnlyTwentyRecentCompletedJobs()
    {
        var jobs = new JobRegistry();
        var active = jobs.Create("Codex", JobExecutionContext.General("/general"), CancellationToken.None);
        Assert.True(jobs.TryStart(active.Snapshot.Id));

        for (var index = 0; index < 25; index++)
        {
            var created = jobs.Create("Claude", JobExecutionContext.General("/general"), CancellationToken.None);
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

    [Fact]
    public void PreservesContextOfSimultaneousJobs()
    {
        var jobs = new JobRegistry();
        var general = jobs.Create("Claude", JobExecutionContext.General("/general"), CancellationToken.None);
        var repository = jobs.Create("Codex", JobExecutionContext.Repository("@fitness_backend", "/repos/fitness"),
            CancellationToken.None);
        Assert.True(jobs.TryStart(general.Snapshot.Id));
        Assert.True(jobs.TryStart(repository.Snapshot.Id));
        var visible = jobs.GetVisible();
        Assert.Equal(JobExecutionMode.General, visible.Single(x => x.Id == general.Snapshot.Id).Context.Mode);
        var repositoryContext = visible.Single(x => x.Id == repository.Snapshot.Id).Context;
        Assert.Equal("@fitness_backend", repositoryContext.RepositoryAlias);
        Assert.Equal("/repos/fitness", repositoryContext.WorkingDirectory);
        jobs.Complete(general.Snapshot.Id, AgentProcessStatus.Succeeded);
        jobs.Complete(repository.Snapshot.Id, AgentProcessStatus.Succeeded);
    }
}
