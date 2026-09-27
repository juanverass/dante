namespace Dante.Worker.Agents;

public interface IClaudeRunner
{
    Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
