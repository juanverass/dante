namespace Dante.Worker.Jobs;

public enum JobExecutionMode { General, Repository }

public sealed record JobExecutionContext(JobExecutionMode Mode, string WorkingDirectory, string? RepositoryAlias)
{
    public string Label => Mode == JobExecutionMode.General ? "General" : RepositoryAlias!;

    public static JobExecutionContext General(string workingDirectory) =>
        new(JobExecutionMode.General, workingDirectory, null);

    public static JobExecutionContext Repository(string alias, string workingDirectory) =>
        new(JobExecutionMode.Repository, workingDirectory, alias);
}
