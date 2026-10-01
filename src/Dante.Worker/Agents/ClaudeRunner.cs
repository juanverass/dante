namespace Dante.Worker.Agents;

public sealed class ClaudeRunner(IAgentProcessExecutor processExecutor) : IClaudeRunner
{
    public Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        bool generalMode = false,
        IReadOnlyDictionary<string, string>? environment = null,
        string? model = null, string? effort = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // The prompt remains a single positional argument, even when it starts with '-'. Without a model the CLI picks
        // its own default (#77).
        string[] modelArguments = model is null ? [] : ["--model", model];
        if (effort is not null && !AgentModelSelection.IsValidEffort(effort))
            throw new ArgumentException("Nome de esforço inválido.", nameof(effort));
        string[] effortArguments = effort is null ? [] : ["--effort", effort];
        var request = new AgentProcessRequest(
            AgentKind.Claude,
            workingDirectory,
            generalMode
                ? ["--print", "--restricted", "--strict-mcp-config", "--tools", "Read,Write,Edit",
                    "--permission-mode", "auto", "--permission-prompts", "none", .. modelArguments, .. effortArguments, "--", prompt]
                : ["--print", "--permission-mode", "auto", "--permission-prompts", "none", .. modelArguments, .. effortArguments, "--",
                    prompt],
            generalMode,
            environment);

        return processExecutor.ExecuteAsync(request, cancellationToken);
    }
}
