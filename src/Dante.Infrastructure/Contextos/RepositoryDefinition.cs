namespace Dante.Infrastructure.Contextos;

// Adapter legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
public sealed record RepositoryDefinition(string Alias, string Path, string? GitHub,
    IReadOnlyList<RepositoryEnvironmentEntry>? Environment = null);

public sealed record RepositoryEnvironmentEntry(string Key, string? LiteralValue, string? HostVariable);
