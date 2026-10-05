namespace Dante.Domain.Contextos;

// Legado movido do Worker na #166: os nomes em inglês ficam até a migração explícita (AD-38).
public sealed record RepositoryDefinition(string Alias, string Path, string? GitHub,
    IReadOnlyList<RepositoryEnvironmentEntry>? Environment = null);

public sealed record RepositoryEnvironmentEntry(string Key, string? LiteralValue, string? HostVariable);

public sealed record ResolvedRepositoryEnvironment(IReadOnlyDictionary<string, string> Values, bool HasSecrets);
