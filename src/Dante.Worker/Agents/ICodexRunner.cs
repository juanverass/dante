using Dante.Worker.Attachments;

namespace Dante.Worker.Agents;

public interface ICodexRunner
{
    Task<AgentProcessResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        bool generalMode = false,
        IReadOnlyDictionary<string, string>? environment = null,
        string? model = null, string? effort = null,
        IReadOnlyList<Attachment>? attachments = null);
}
