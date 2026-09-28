namespace Dante.Worker.Repositories;

public sealed record RepositoryDefinition(string Alias, string Path, string? GitHub,
    IReadOnlyList<RepositoryEnvironmentEntry>? Environment = null);

public sealed record RepositoryEnvironmentEntry(string Key, string? LiteralValue, string? HostVariable);

public sealed record ResolvedRepositoryEnvironment(IReadOnlyDictionary<string, string> Values, bool HasSecrets);
