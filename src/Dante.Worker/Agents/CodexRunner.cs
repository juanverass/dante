using Dante.Worker.Attachments;

namespace Dante.Worker.Agents;

public sealed class CodexRunner(IAgentProcessExecutor processExecutor) : ICodexRunner
{
    public Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        bool generalMode = false,
        IReadOnlyDictionary<string, string>? environment = null,
        string? model = null, string? effort = null,
        IReadOnlyList<Attachment>? attachments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        // ArgumentList keeps the prompt as one data argument, even if it starts with '-'. Without a model the CLI picks
        // its own default (#77).
        string[] modelArguments = model is null ? [] : ["--model", model];
        if (effort is not null && !AgentModelSelection.IsValidEffort(effort))
            throw new ArgumentException("Nome de esforço inválido.", nameof(effort));
        string[] effortArguments = effort is null ? [] : ["--config", $"model_reasoning_effort=\"{effort}\""];
        // One -i per image, in order, before the separator (#93 spike, AD-29).
        attachments ??= [];
        if (attachments.Any(attachment => attachment.Kind != AttachmentKind.Image))
            throw new ArgumentException("O Codex só recebe imagens como anexo.", nameof(attachments));
        string[] imageArguments = [.. attachments.SelectMany(attachment => new[] { "-i", attachment.Path })];
        var request = new AgentProcessRequest(
            AgentKind.Codex,
            workingDirectory,
            generalMode
                ? ["exec", "--sandbox", "workspace-write", "--skip-git-repo-check", "--ignore-user-config",
                    .. modelArguments, .. effortArguments, .. imageArguments, "--", prompt]
                : ["exec", "--approve-for-me", .. modelArguments, .. effortArguments, .. imageArguments, "--", prompt],
            generalMode,
            environment);

        return processExecutor.ExecuteAsync(request, cancellationToken);
    }
}
