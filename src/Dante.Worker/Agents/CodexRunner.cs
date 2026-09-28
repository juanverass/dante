namespace Dante.Worker.Agents;

public sealed class CodexRunner(IAgentProcessExecutor processExecutor) : ICodexRunner
{
    public Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        bool generalMode = false,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // ArgumentList keeps the prompt as one data argument, even if it starts with '-'.
        var request = new AgentProcessRequest(
            AgentKind.Codex,
            workingDirectory,
            generalMode
                ? ["exec", "--sandbox", "workspace-write", "--skip-git-repo-check", "--ignore-user-config",
                    "--", prompt]
                : ["exec", "--approve-for-me", "--", prompt],
            generalMode,
            environment);

        return processExecutor.ExecuteAsync(request, cancellationToken);
    }
}
