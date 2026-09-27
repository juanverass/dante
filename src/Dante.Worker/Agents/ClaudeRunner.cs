namespace Dante.Worker.Agents;

public sealed class ClaudeRunner(IAgentProcessExecutor processExecutor) : IClaudeRunner
{
    public Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // The prompt remains a single positional argument, even when it starts with '-'.
        var request = new AgentProcessRequest(
            AgentKind.Claude,
            workingDirectory,
            ["--print", "--permission-mode", "auto", "--permission-prompts", "none", "--", prompt]);

        return processExecutor.ExecuteAsync(request, cancellationToken);
    }
}
