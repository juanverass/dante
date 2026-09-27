namespace Dante.Worker.Agents;

public sealed class CodexRunner(IAgentProcessExecutor processExecutor) : ICodexRunner
{
    public Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // ArgumentList keeps the prompt as one data argument, even if it starts with '-'.
        var request = new AgentProcessRequest(
            AgentKind.Codex,
            workingDirectory,
            ["exec", "--sandbox", "workspace-write", "--approve-for-me", "--", prompt]);

        return processExecutor.ExecuteAsync(request, cancellationToken);
    }
}
