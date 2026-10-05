using Dante.Application.Anexos;

namespace Dante.Application.Agentes;

// Legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
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
