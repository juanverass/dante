namespace Dante.Worker.Agents;

public sealed class ClaudeRunner(IAgentProcessExecutor processExecutor) : IClaudeRunner
{
    public Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        bool generalMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // The prompt remains a single positional argument, even when it starts with '-'.
        var request = new AgentProcessRequest(
            AgentKind.Claude,
            workingDirectory,
            generalMode
                ? ["--print", "--restricted", "--strict-mcp-config", "--tools", "Read,Write,Edit",
                    "--permission-mode", "auto", "--permission-prompts", "none", "--", prompt]
                : ["--print", "--permission-mode", "auto", "--permission-prompts", "none", "--", prompt],
            generalMode);

        return processExecutor.ExecuteAsync(request, cancellationToken);
    }
}
