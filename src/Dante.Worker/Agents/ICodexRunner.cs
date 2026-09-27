namespace Dante.Worker.Agents;

public interface ICodexRunner
{
    Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
