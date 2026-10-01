using Dante.Worker.Agents;
using Dante.Worker.Repositories;
using Dante.Worker.Settings;

namespace Dante.Worker.Jobs;

public enum AgentSource { Explicit, Default }

public enum ContextSource { Explicit, Active, General }

public enum ContextResolutionFailure
{
    None,
    EmptyPrompt,
    InvalidAlias,
    UnknownAlias,
    StaleActiveRepository,
    Environment,
    GeneralWorkspaceOverlap
}

public sealed record AgentContextResolution(
    AgentKind Agent,
    AgentSource AgentSource,
    JobExecutionContext? Context,
    ContextSource ContextSource,
    ResolvedRepositoryEnvironment? Environment,
    string Prompt,
    ContextResolutionFailure Failure = ContextResolutionFailure.None,
    string? Error = null)
{
    public bool Succeeded => Failure == ContextResolutionFailure.None;
}

// Single place that decides which agent and context run a message (#37). Agent: /claude or /codex, then the default
// agent. Context: an explicit @alias, then the user's active repository, then General Mode. Overrides apply to one
// execution only and are never inferred from the prompt text; any refusal means no agent may start.
public sealed class AgentContextResolver(
    GeneralWorkspace generalWorkspace,
    RepositoryRegistry? repositories = null,
    AssistantSettingsStore? settings = null)
{
    // The text after the command: a leading @alias is the context override and is removed from the prompt.
    public AgentContextResolution Resolve(long userId, AgentKind? explicitAgent, string text)
    {
        text = text.Trim();
        string? alias = null;
        if (text.StartsWith('@'))
        {
            var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
            alias = separator < 0 ? text : text[..separator];
            text = separator < 0 ? string.Empty : text[(separator + 1)..].Trim();
        }

        if (text.Length == 0)
        {
            var (agent, agentSource) = ResolveAgent(explicitAgent);
            return new AgentContextResolution(agent, agentSource, null,
                alias is null ? ContextSource.General : ContextSource.Explicit, null, string.Empty,
                ContextResolutionFailure.EmptyPrompt);
        }
        return ResolveContext(userId, explicitAgent, alias) with { Prompt = text };
    }

    // For entry points that carry the alias apart from any prompt, like /session start.
    public AgentContextResolution ResolveContext(long userId, AgentKind? explicitAgent, string? alias)
    {
        var (agent, agentSource) = ResolveAgent(explicitAgent);
        var source = alias is not null ? ContextSource.Explicit : ContextSource.Active;
        alias ??= settings?.GetActiveRepository(userId);
        if (alias is null)
        {
            var path = generalWorkspace.Path;
            if (repositories?.List().Any(repository => IsWithin(path, repository.Path) ||
                    IsWithin(repository.Path, path)) == true)
                return Refuse(ContextResolutionFailure.GeneralWorkspaceOverlap,
                    "O workspace geral coincide com um repositório cadastrado; configure DANTE_GENERAL_WORKSPACE fora dos projetos.");
            return new AgentContextResolution(agent, agentSource, JobExecutionContext.General(path),
                ContextSource.General, null, string.Empty);
        }

        RepositoryDefinition? repository;
        try { repository = repositories?.Get(alias); }
        catch (ArgumentException) { return Refuse(ContextResolutionFailure.InvalidAlias, "Alias inválido."); }
        if (repository is null)
        {
            // A stale active repository requires a new selection instead of silently falling back to General (AD-14).
            return source == ContextSource.Active
                ? Refuse(ContextResolutionFailure.StaleActiveRepository,
                    $"O repositório ativo {alias} não está mais cadastrado. Use /use @alias ou /use general.")
                : Refuse(ContextResolutionFailure.UnknownAlias, $"Repositório {alias} não cadastrado. Veja /repos.");
        }

        try
        {
            return new AgentContextResolution(agent, agentSource,
                JobExecutionContext.Repository(repository.Alias, repository.Path), source,
                repositories!.ResolveEnvironment(repository.Alias), string.Empty);
        }
        catch (InvalidOperationException exception)
        {
            return Refuse(ContextResolutionFailure.Environment, exception.Message);
        }

        AgentContextResolution Refuse(ContextResolutionFailure failure, string error) =>
            new(agent, agentSource, null, source, null, string.Empty, failure, error);
    }

    private (AgentKind Agent, AgentSource Source) ResolveAgent(AgentKind? explicitAgent) =>
        explicitAgent is { } agent ? (agent, AgentSource.Explicit)
            : ((settings?.Current ?? AssistantSettings.Default).DefaultAgent, AgentSource.Default);

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative));
    }
}
