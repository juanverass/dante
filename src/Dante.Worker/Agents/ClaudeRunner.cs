using Dante.Worker.Attachments;

namespace Dante.Worker.Agents;

public sealed class ClaudeRunner(IAgentProcessExecutor processExecutor) : IClaudeRunner
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

        // The prompt remains a single positional argument, even when it starts with '-'. Without a model the CLI picks
        // its own default (#77).
        string[] modelArguments = model is null ? [] : ["--model", model];
        if (effort is not null && !AgentModelSelection.IsValidEffort(effort))
            throw new ArgumentException("Nome de esforço inválido.", nameof(effort));
        string[] effortArguments = effort is null ? [] : ["--effort", effort];
        // Images are files the agent opens with Read: --add-dir allows only their directory, outside the workspace,
        // and the prompt lists the absolute paths in order (#93 spike, AD-29). General Mode stays restricted (AD-09).
        attachments ??= [];
        if (attachments.Any(attachment => attachment.Kind != AttachmentKind.Image))
            throw new ArgumentException("O Claude só recebe imagens como anexo.", nameof(attachments));
        string[] attachmentArguments = [.. attachments.Select(attachment => Path.GetDirectoryName(attachment.Path)!)
            .Distinct(StringComparer.Ordinal).SelectMany(directory => new[] { "--add-dir", directory })];
        if (attachments.Count > 0)
            prompt += "\n\nImagens anexadas, na ordem (abra cada uma com a ferramenta Read):\n" + string.Join('\n',
                attachments.Select((attachment, index) => $"{index + 1}. {attachment.Path}" +
                    (attachment.Name is null ? string.Empty : $" ({attachment.Name})")));
        var request = new AgentProcessRequest(
            AgentKind.Claude,
            workingDirectory,
            generalMode
                ? ["--print", "--restricted", "--strict-mcp-config", "--tools", "Read,Write,Edit",
                    "--permission-mode", "auto", "--permission-prompts", "none", .. modelArguments, .. effortArguments,
                    .. attachmentArguments, "--", prompt]
                : ["--print", "--permission-mode", "auto", "--permission-prompts", "none", .. modelArguments, .. effortArguments,
                    .. attachmentArguments, "--", prompt],
            generalMode,
            environment);

        return processExecutor.ExecuteAsync(request, cancellationToken);
    }
}
